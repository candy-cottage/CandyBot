using System.Data;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;
using Milimoe.FunGame.Core.Api.Transmittal;

namespace CandyCottage.CandyBot.Services
{
    /// <summary>
    /// 单次「按来源」同步协调的结果统计（每个 fileId 一份）。
    /// <list type="bullet">
    ///   <item><b>Inserted</b> 新增的同步行（NEW）。</item>
    ///   <item><b>UpdatedInPlace</b> 命中非人手维护行、源变 → 就地更新显示列（MATCHED-CHANGED, IsManual=0）。</item>
    ///   <item><b>Cached</b> 命中人手维护行、指纹变 → 写缓存并置「有更新」（MATCHED-CHANGED, IsManual=1）。</item>
    ///   <item><b>Expired</b> 完整快照下未命中的人手维护行 → 置「已过时」。</item>
    ///   <item><b>Deleted</b> 完整快照下未命中的非人手维护行 → 删除。</item>
    ///   <item><b>ErrorMessage</b> 协调整体失败时的描述（已回滚）；成功为 null。</item>
    /// </list>
    /// </summary>
    public record ForzaSyncReport(string FileId, long ActorUserId, bool IsCompleteSnapshot,
        int Inserted, int UpdatedInPlace, int Cached, int Expired, int Deleted, int ParsedCount, string? ErrorMessage);

    /// <summary>
    /// 调校同步协调引擎：把一次解析得到的某来源（fileId）的全部调校行，与库中该来源既有行做幂等协调。
    ///
    /// <para>设计目标是<b>绝不像旧的「清空+重灌」那样破坏数据</b>：人手维护行的显示列永不被同步覆盖；
    /// 仅在「完整快照」前提下才允许对未命中行做删除/置过时；按 (SourceIdentity) 分组并按指纹精确配对，
    /// 使管理员改键、孪生行、精确重复等情形都不误配、不撞唯一键。</para>
    /// </summary>
    public class ForzaSyncService(ILogger<ForzaSyncService> logger, SQLHelper sql, ForzaService forzaService)
    {
        /// <summary>同步行的展示列（既地更新与缓存捕获共用）。</summary>
        private const string TuneStatusNormal = "正常";
        private const string TuneStatusUpdated = "有更新";
        private const string TuneStatusExpired = "已过时";

        /// <summary>从库中读出的既有调校行的精简快照（脱离 DataSet，因后续查询会重置 DataSet）。</summary>
        private sealed record ExistingTune(int Id, string SourceIdentity, int SourceIdentitySeq,
            string? SourceFingerprint, bool IsManual, int VehicleId,
            string Class, string MatchType, string? Drivetrain, string? PerformancePreference,
            string? TireCompound, string? SurfaceCondition, string? Strength, string? Tuner,
            string? TuneRaw, string? Remarks, string? LapTime, string? ExtData,
            string? SourceSheet, int? SourceRow, string? SourceDisplayName);

        /// <summary>对每条 parsed 预先算好的标识三元组：归一化身份键、指纹、modelKey。</summary>
        private sealed record ParsedKeys(VehicleTuneRequest Tune, string Identity, string Fingerprint, string ModelKey);

        /// <summary>
        /// 协调某来源（fileId）的解析结果与库中既有行。详见类型文档与 §8 配对、决策表。
        ///
        /// <para><b>两阶段事务模型（避免事务嵌套 bug）：</b></para>
        /// <list type="number">
        ///   <item>Phase 1（车型解析，在开启本协调事务<b>之前</b>）：对 parsed 中每个不同 ModelKey
        ///   调用 <see cref="ForzaService.EnsureVehicleModel"/>（其自带事务、车型只增）建立 VehicleId 映射，
        ///   并回填每条 parsed 的 <c>VehicleId</c> 与 <c>SourceFileId</c>。必须在本事务前完成——否则
        ///   EnsureVehicleModel 内部的 NewTransaction/Commit 会覆盖并提交掉我们的外层事务。</item>
        ///   <item>Phase 2（调校协调，在<b>一个</b>由本方法拥有的事务内）：以 <c>FOR UPDATE</c> 行锁载入
        ///   该 fileId 的既有行；新增走 <see cref="ForzaService.InsertTune"/>（不开自身事务，跑在本事务内），
        ///   更新/缓存/删除走直接参数化 SQL（不调用会自开事务的 UpdateTune/DeleteTunesByIds）；
        ///   成功 Commit，异常或失败在 finally 中 Rollback。</item>
        /// </list>
        /// 哨兵/迁移行（SourceFileId='migration'）因 fileId 不同，天然被 WHERE 排除，不参与协调。
        /// </summary>
        internal ForzaSyncReport Reconcile(IReadOnlyList<VehicleTuneRequest> parsed, string fileId, bool isCompleteSnapshot, long actorUserId)
        {
            int inserted = 0, updatedInPlace = 0, cached = 0, expired = 0, deleted = 0;

            // ---- Phase 1：车型解析（本事务之前，EnsureVehicleModel 自带事务，车型只增）----
            List<ParsedKeys> keyed;
            try
            {
                keyed = ResolveVehiclesAndKeys(parsed, fileId, actorUserId, out string phase1Err);
                if (phase1Err.Length > 0)
                    return Fail(fileId, actorUserId, isCompleteSnapshot, parsed.Count, phase1Err);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reconcile Phase1 error: {Message}", ex.Message);
                return Fail(fileId, actorUserId, isCompleteSnapshot, parsed.Count, $"车型解析异常：{ex.Message}");
            }

            // ---- Phase 2：调校协调（单一事务）----
            try
            {
                sql.NewTransaction();

                // 行锁载入该来源既有行并立即物化（后续写操作会重置 DataSet）
                sql.Parameters["@fileId"] = fileId;
                sql.ExecuteDataSetForUpdate("SELECT * FROM VehicleTunes WHERE SourceFileId = @fileId");
                List<ExistingTune> existing = MaterializeExisting(sql.DataSet);

                // 按 SourceIdentity 分组（既有 + 解析）。遍历两侧 identity 全集：
                // 既有但 parsed 无对应 → 纯未命中组；parsed 有 → 走配对/决策。
                Dictionary<string, List<ExistingTune>> existingByIdentity = GroupExisting(existing);
                Dictionary<string, List<ParsedKeys>> parsedByIdentity = GroupParsed(keyed);
                HashSet<string> allIdentities = [.. existingByIdentity.Keys, .. parsedByIdentity.Keys];

                foreach (string identity in allIdentities)
                {
                    existingByIdentity.TryGetValue(identity, out List<ExistingTune>? eGroup);
                    parsedByIdentity.TryGetValue(identity, out List<ParsedKeys>? pGroup);

                    // 配对 + 决策，并取回该组真正未命中的既有行（fingerprint 配对可能命中非首行，
                    // 故不能靠按 Id 截断推断未命中集，必须由配对过程显式回传）。
                    if (!ReconcileIdentityGroup(eGroup ?? [], pGroup ?? [], fileId, actorUserId,
                            ref inserted, ref updatedInPlace, ref cached,
                            out List<ExistingTune> missing, out string err))
                        return RollbackFail(fileId, actorUserId, isCompleteSnapshot, parsed.Count, err);

                    // 未命中既有行：仅完整快照下处理（非人手删除 / 人手置已过时）；否则保留不动（机制2）。
                    if (isCompleteSnapshot)
                    {
                        foreach (ExistingTune e in missing)
                        {
                            if (!HandleMissing(e, ref expired, ref deleted, out err))
                                return RollbackFail(fileId, actorUserId, isCompleteSnapshot, parsed.Count, err);
                        }
                    }
                }

                sql.Commit();
                return new ForzaSyncReport(fileId, actorUserId, isCompleteSnapshot,
                    inserted, updatedInPlace, cached, expired, deleted, parsed.Count, null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reconcile Phase2 error: {Message}", ex.Message);
                return Fail(fileId, actorUserId, isCompleteSnapshot, parsed.Count, $"协调异常：{ex.Message}");
            }
            finally
            {
                sql.Rollback();
            }
        }

        // ==================== Phase 1 辅助 ====================

        /// <summary>
        /// 对 parsed 中每个不同 ModelKey 调用 EnsureVehicleModel 建立 VehicleId 映射，
        /// 回填每条 parsed 的 VehicleId / SourceFileId，并返回预算好的 (identity, fingerprint, modelKey)。
        /// 任一车型 upsert 失败即通过 error 报错（调用方据此短路）。
        /// </summary>
        private List<ParsedKeys> ResolveVehiclesAndKeys(IReadOnlyList<VehicleTuneRequest> parsed, string fileId, long actorUserId, out string error)
        {
            error = "";
            List<ParsedKeys> result = new(parsed.Count);
            Dictionary<string, int> vehicleIdByModelKey = [];

            foreach (VehicleTuneRequest tune in parsed)
            {
                string modelKey = ForzaTuneIdentity.BuildModelKey(tune.Manufacturer, tune.Model, tune.Year);

                if (!vehicleIdByModelKey.TryGetValue(modelKey, out int vehicleId))
                {
                    // 同步路径：车型由系统创建（actorUserId 传 0 → IsManual=0），不覆写既有车型 IsManual。
                    // 注：EnsureVehicleModel 自带事务，必须在本协调事务之前调用。
                    vehicleId = forzaService.EnsureVehicleModel(
                        new VehicleModelRequest { Manufacturer = tune.Manufacturer, Model = tune.Model, Year = tune.Year },
                        0, out string modelErr);
                    if (vehicleId <= 0)
                    {
                        error = modelErr.Length > 0 ? modelErr : "车型解析失败。";
                        return result;
                    }
                    vehicleIdByModelKey[modelKey] = vehicleId;
                }

                tune.VehicleId = vehicleId;
                tune.SourceFileId = fileId;

                string identity = ForzaTuneIdentity.BuildSourceIdentity(tune, modelKey, fileId);
                string fingerprint = ForzaTuneIdentity.BuildFingerprint(tune, modelKey);
                result.Add(new ParsedKeys(tune, identity, fingerprint, modelKey));
            }

            return result;
        }

        // ==================== Phase 2 辅助 ====================

        /// <summary>把 FOR UPDATE 查询出的既有行物化为脱离 DataSet 的快照列表（后续写操作会重置 DataSet）。</summary>
        private static List<ExistingTune> MaterializeExisting(DataSet ds)
        {
            List<ExistingTune> rows = [];
            if (ds.Tables.Count == 0) return rows;

            foreach (DataRow r in ds.Tables[0].Rows)
            {
                rows.Add(new ExistingTune(
                    Id: Convert.ToInt32(r["Id"]),
                    SourceIdentity: r["SourceIdentity"]?.ToString() ?? "",
                    SourceIdentitySeq: Convert.ToInt32(r["SourceIdentitySeq"]),
                    SourceFingerprint: Str(r["SourceFingerprint"]),
                    IsManual: Convert.ToInt32(r["IsManual"]) != 0,
                    VehicleId: Convert.ToInt32(r["VehicleId"]),
                    Class: r["Class"]?.ToString() ?? "",
                    MatchType: r["MatchType"]?.ToString() ?? "",
                    Drivetrain: Str(r["Drivetrain"]),
                    PerformancePreference: Str(r["PerformancePreference"]),
                    TireCompound: Str(r["TireCompound"]),
                    SurfaceCondition: Str(r["SurfaceCondition"]),
                    Strength: Str(r["Strength"]),
                    Tuner: Str(r["Tuner"]),
                    TuneRaw: Str(r["TuneRaw"]),
                    Remarks: Str(r["Remarks"]),
                    LapTime: Str(r["LapTime"]),
                    ExtData: Str(r["ExtData"]),
                    SourceSheet: Str(r["SourceSheet"]),
                    SourceRow: r["SourceRow"] == DBNull.Value ? null : Convert.ToInt32(r["SourceRow"]),
                    SourceDisplayName: Str(r["SourceDisplayName"])));
            }
            return rows;
        }

        private static string? Str(object? v) => v == null || v == DBNull.Value ? null : v.ToString();

        private static Dictionary<string, List<ExistingTune>> GroupExisting(List<ExistingTune> existing)
        {
            Dictionary<string, List<ExistingTune>> map = [];
            foreach (ExistingTune e in existing)
            {
                if (!map.TryGetValue(e.SourceIdentity, out List<ExistingTune>? list))
                    map[e.SourceIdentity] = list = [];
                list.Add(e);
            }
            return map;
        }

        private static Dictionary<string, List<ParsedKeys>> GroupParsed(List<ParsedKeys> keyed)
        {
            Dictionary<string, List<ParsedKeys>> map = [];
            foreach (ParsedKeys p in keyed)
            {
                if (!map.TryGetValue(p.Identity, out List<ParsedKeys>? list))
                    map[p.Identity] = list = [];
                list.Add(p);
            }
            return map;
        }

        /// <summary>
        /// §8 配对 + 决策（针对单个 identity 组）：
        /// 1) 指纹配对：每条 parsed 与一条未配对、指纹相同的既有行配 → MATCHED-UNCHANGED（无写）。
        /// 2) 位置配对：剩余既有行（按 Id 升序）与剩余 parsed（解析序）依次配 → MATCHED-CHANGED。
        ///    - 既有非人手 → 就地更新显示列；既有人手 → 写缓存并置「有更新」（live 显示不动）。
        /// 3) 多出的 parsed → NEW（InsertTune，seq = 组内最大既有 seq + 1，逐条递增）。
        /// 4) 多出的既有行 → MISSING（由调用方在完整快照阶段统一处理删除/过时）。
        /// </summary>
        private bool ReconcileIdentityGroup(
            List<ExistingTune> eGroup, List<ParsedKeys> pGroup, string fileId, long actorUserId,
            ref int inserted, ref int updatedInPlace, ref int cached,
            out List<ExistingTune> missing, out string error)
        {
            error = "";
            missing = [];

            List<ExistingTune> eSorted = [.. eGroup.OrderBy(e => e.Id)];
            bool[] eMatched = new bool[eSorted.Count];
            bool[] pMatched = new bool[pGroup.Count];

            // 1) 指纹配对（精确）：未配对 parsed → 未配对、指纹相同的既有行
            for (int pi = 0; pi < pGroup.Count; pi++)
            {
                for (int ei = 0; ei < eSorted.Count; ei++)
                {
                    if (eMatched[ei]) continue;
                    if (eSorted[ei].SourceFingerprint == pGroup[pi].Fingerprint)
                    {
                        eMatched[ei] = true;
                        pMatched[pi] = true;
                        break; // MATCHED-UNCHANGED：无 DB 写
                    }
                }
            }

            // 2) 位置配对：剩余既有行（已按 Id 升序） zip 剩余 parsed（解析序）
            List<int> eRem = [];
            for (int ei = 0; ei < eSorted.Count; ei++) if (!eMatched[ei]) eRem.Add(ei);
            List<int> pRem = [];
            for (int pi = 0; pi < pGroup.Count; pi++) if (!pMatched[pi]) pRem.Add(pi);

            int zip = Math.Min(eRem.Count, pRem.Count);
            for (int k = 0; k < zip; k++)
            {
                ExistingTune e = eSorted[eRem[k]];
                ParsedKeys p = pGroup[pRem[k]];
                eMatched[eRem[k]] = true;
                pMatched[pRem[k]] = true;

                if (!e.IsManual)
                {
                    if (!UpdateInPlace(e.Id, p, actorUserId, out error)) return false;
                    updatedInPlace++;
                }
                else
                {
                    if (!WriteCacheAndMarkUpdated(e.Id, p, fileId, out error)) return false;
                    cached++;
                }
            }

            // 3) 剩余 parsed → NEW；seq 从「组内最大既有 seq + 1」起逐条递增（既有空则从 0 起）
            int nextSeq = eGroup.Count > 0 ? eGroup.Max(e => e.SourceIdentitySeq) + 1 : 0;
            for (int k = zip; k < pRem.Count; k++)
            {
                ParsedKeys p = pGroup[pRem[k]];
                VehicleTuneRequest req = p.Tune;
                req.IsManual = false;
                req.TuneStatus = TuneStatusNormal;
                req.LastUpdatedUserId = actorUserId;

                int newId = forzaService.InsertTune(req, nextSeq, out error);
                if (newId <= 0) return false;
                nextSeq++;
                inserted++;
            }

            // 4) 剩余既有行（eMatched 中仍为 false）即 MISSING——显式回传给调用方按完整快照决策。
            for (int ei = 0; ei < eSorted.Count; ei++)
                if (!eMatched[ei]) missing.Add(eSorted[ei]);
            return true;
        }

        /// <summary>MATCHED-CHANGED + 非人手：就地更新全部显示列，SourceIdentity 冻结，状态回正常。</summary>
        private bool UpdateInPlace(int tuneId, ParsedKeys p, long actorUserId, out string error)
        {
            error = "";
            VehicleTuneRequest t = p.Tune;
            string tuner = ForzaTuneIdentity.TunerKey(ForzaTuneIdentity.ParseTuners(t.TuneRaw));

            sql.Parameters["@class"] = t.Class ?? (object)DBNull.Value;
            sql.Parameters["@matchType"] = t.MatchType ?? (object)DBNull.Value;
            sql.Parameters["@drivetrain"] = (object?)t.Drivetrain ?? DBNull.Value;
            sql.Parameters["@perfPref"] = (object?)t.PerformancePreference ?? DBNull.Value;
            sql.Parameters["@tireCompound"] = (object?)t.TireCompound ?? DBNull.Value;
            sql.Parameters["@surfCond"] = (object?)t.SurfaceCondition ?? DBNull.Value;
            sql.Parameters["@strength"] = (object?)t.Strength ?? DBNull.Value;
            sql.Parameters["@tuner"] = string.IsNullOrEmpty(tuner) ? DBNull.Value : tuner;
            sql.Parameters["@tuneRaw"] = (object?)t.TuneRaw ?? DBNull.Value;
            sql.Parameters["@remarks"] = (object?)t.Remarks ?? DBNull.Value;
            sql.Parameters["@lapTime"] = (object?)t.LapTime ?? DBNull.Value;
            sql.Parameters["@extData"] = (object?)t.ExtData ?? DBNull.Value;
            sql.Parameters["@fingerprint"] = p.Fingerprint;
            sql.Parameters["@status"] = TuneStatusNormal;
            sql.Parameters["@userId"] = actorUserId;
            sql.Parameters["@id"] = tuneId;

            sql.Execute(@"
                UPDATE VehicleTunes SET
                    Class = @class, MatchType = @matchType, Drivetrain = @drivetrain,
                    PerformancePreference = @perfPref, TireCompound = @tireCompound,
                    SurfaceCondition = @surfCond, Strength = @strength, Tuner = @tuner,
                    TuneRaw = @tuneRaw, Remarks = @remarks, LapTime = @lapTime, ExtData = @extData,
                    SourceFingerprint = @fingerprint, TuneStatus = @status,
                    LastUpdatedUserId = @userId, LastUpdatedAt = NOW()
                WHERE Id = @id");

            if (!sql.Success)
            {
                error = $"就地更新调校失败（Id={tuneId}）。";
                return false;
            }
            return true;
        }

        /// <summary>
        /// MATCHED-CHANGED + 人手：把新源快照写入/刷新缓存（uq_cache_tune 幂等 upsert），
        /// 并把 live 行置「有更新」。<b>不触碰</b> live 行的展示列。
        /// </summary>
        private bool WriteCacheAndMarkUpdated(int tuneId, ParsedKeys p, string fileId, out string error)
        {
            error = "";
            VehicleTuneRequest t = p.Tune;
            string tuner = ForzaTuneIdentity.TunerKey(ForzaTuneIdentity.ParseTuners(t.TuneRaw));

            sql.Parameters["@tuneId"] = tuneId;
            sql.Parameters["@vehicleId"] = t.VehicleId ?? 0;
            sql.Parameters["@class"] = t.Class ?? (object)DBNull.Value;
            sql.Parameters["@matchType"] = t.MatchType ?? (object)DBNull.Value;
            sql.Parameters["@drivetrain"] = (object?)t.Drivetrain ?? DBNull.Value;
            sql.Parameters["@perfPref"] = (object?)t.PerformancePreference ?? DBNull.Value;
            sql.Parameters["@tireCompound"] = (object?)t.TireCompound ?? DBNull.Value;
            sql.Parameters["@surfCond"] = (object?)t.SurfaceCondition ?? DBNull.Value;
            sql.Parameters["@strength"] = (object?)t.Strength ?? DBNull.Value;
            sql.Parameters["@tuner"] = string.IsNullOrEmpty(tuner) ? DBNull.Value : tuner;
            sql.Parameters["@tuneRaw"] = (object?)t.TuneRaw ?? DBNull.Value;
            sql.Parameters["@remarks"] = (object?)t.Remarks ?? DBNull.Value;
            sql.Parameters["@lapTime"] = (object?)t.LapTime ?? DBNull.Value;
            sql.Parameters["@extData"] = (object?)t.ExtData ?? DBNull.Value;
            sql.Parameters["@sourceFileId"] = fileId;
            sql.Parameters["@sourceSheet"] = (object?)t.SourceSheet ?? DBNull.Value;
            sql.Parameters["@sourceRow"] = t.SourceRow.HasValue ? t.SourceRow.Value : (object)DBNull.Value;
            sql.Parameters["@sourceDisplayName"] = (object?)t.SourceDisplayName ?? DBNull.Value;
            sql.Parameters["@sourceIdentity"] = p.Identity;
            sql.Parameters["@fingerprint"] = p.Fingerprint;

            sql.Execute(@"
                INSERT INTO VehicleTuneCache
                    (TuneId, VehicleId, Class, MatchType, Drivetrain, PerformancePreference,
                     TireCompound, SurfaceCondition, Strength, Tuner, TuneRaw, Remarks, LapTime, ExtData,
                     SourceFileId, SourceSheet, SourceRow, SourceDisplayName, SourceIdentity, SourceFingerprint)
                VALUES
                    (@tuneId, @vehicleId, @class, @matchType, @drivetrain, @perfPref,
                     @tireCompound, @surfCond, @strength, @tuner, @tuneRaw, @remarks, @lapTime, @extData,
                     @sourceFileId, @sourceSheet, @sourceRow, @sourceDisplayName, @sourceIdentity, @fingerprint)
                ON DUPLICATE KEY UPDATE
                    VehicleId = VALUES(VehicleId), Class = VALUES(Class), MatchType = VALUES(MatchType),
                    Drivetrain = VALUES(Drivetrain), PerformancePreference = VALUES(PerformancePreference),
                    TireCompound = VALUES(TireCompound), SurfaceCondition = VALUES(SurfaceCondition),
                    Strength = VALUES(Strength), Tuner = VALUES(Tuner), TuneRaw = VALUES(TuneRaw),
                    Remarks = VALUES(Remarks), LapTime = VALUES(LapTime), ExtData = VALUES(ExtData),
                    SourceFileId = VALUES(SourceFileId), SourceSheet = VALUES(SourceSheet),
                    SourceRow = VALUES(SourceRow), SourceDisplayName = VALUES(SourceDisplayName),
                    SourceIdentity = VALUES(SourceIdentity), SourceFingerprint = VALUES(SourceFingerprint),
                    CapturedAt = NOW()");

            if (!sql.Success)
            {
                error = $"写入调校缓存失败（TuneId={tuneId}）。";
                return false;
            }

            sql.Parameters["@status"] = TuneStatusUpdated;
            sql.Parameters["@id"] = tuneId;
            sql.Execute("UPDATE VehicleTunes SET TuneStatus = @status WHERE Id = @id");
            if (!sql.Success)
            {
                error = $"置「有更新」失败（Id={tuneId}）。";
                return false;
            }
            return true;
        }

        /// <summary>
        /// MISSING（仅完整快照下调用）：人手维护行置「已过时」；非人手行删除（连带删缓存行）。
        /// </summary>
        private bool HandleMissing(ExistingTune e, ref int expired, ref int deleted, out string error)
        {
            error = "";
            if (e.IsManual)
            {
                sql.Parameters["@status"] = TuneStatusExpired;
                sql.Parameters["@id"] = e.Id;
                sql.Execute("UPDATE VehicleTunes SET TuneStatus = @status WHERE Id = @id");
                if (!sql.Success)
                {
                    error = $"置「已过时」失败（Id={e.Id}）。";
                    return false;
                }
                expired++;
            }
            else
            {
                sql.Parameters["@id"] = e.Id;
                sql.Execute("DELETE FROM VehicleTunes WHERE Id = @id");
                if (!sql.Success)
                {
                    error = $"删除未命中调校失败（Id={e.Id}）。";
                    return false;
                }
                sql.Parameters["@cid"] = e.Id;
                sql.Execute("DELETE FROM VehicleTuneCache WHERE TuneId = @cid");
                // 缓存删除影响 0 行是正常的（多数行无缓存），不据 Success 判失败。
                deleted++;
            }
            return true;
        }

        // ==================== 失败构造 ====================

        private static ForzaSyncReport Fail(string fileId, long actorUserId, bool isComplete, int parsedCount, string error)
            => new(fileId, actorUserId, isComplete, 0, 0, 0, 0, 0, parsedCount, error);

        /// <summary>Phase 2 内失败：立即回滚（finally 仍会再次 Rollback，幂等无害），返回失败报告。</summary>
        private ForzaSyncReport RollbackFail(string fileId, long actorUserId, bool isComplete, int parsedCount, string error)
        {
            sql.Rollback();
            return Fail(fileId, actorUserId, isComplete, parsedCount, error);
        }
    }
}
