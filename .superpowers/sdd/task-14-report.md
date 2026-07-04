# Task 14 Report

## TDD Evidence

### Integration tests (new, `ForzaServiceTests.cs`)

**Validation / error-path tests:**
- `EnsureVehicleModel_缺少Manufacturer返回0且错误消息正确`
- `EnsureVehicleModel_缺少Model返回0且错误消息正确`
- `AddTune_缺少VehicleId返回0且错误消息正确`
- `AddTune_VehicleId不存在返回0且错误消息包含不存在`
- `AddTune_缺少Class返回0且错误消息正确`
- `AddTune_缺少MatchType返回0且错误消息正确`
- `AddTune_缺少Drivetrain返回0且错误消息正确`

**Happy-path / DB-state tests:**
- `AddTune_合法_插入成功且IsManual为1且SourceIdentity为空`
  Seeds vehicle model via `EnsureVehicleModel`, then calls `AddTune`; asserts returned `tuneId > 0`, queries DB directly and asserts `IsManual=1`, `SourceIdentity=""`, `SourceFileId=""`.
- `AddTune_同车型多条手动调校_seq递增不撞唯一键`
  Calls `AddTune` 3 times on same vehicle; asserts all 3 return distinct `tuneId > 0` and DB has 3 rows with distinct `SourceIdentitySeq` values 0, 1, 2.

**OR/AND SQL probe fixes:**
- `查车_OR条件_第一个条件匹配返回正确结果` / `查车_AND条件_双条件均匹配才返回结果` — changed `FROM Vehicles WHERE Class=...` to `FROM VehicleTunes WHERE Class=...` to match the new schema.

**Removed (schema-incompatible with new split tables):**
- `InsertVehicle_不开启自身事务_可由外层事务回滚` — this test inserted directly into old flat `Vehicles` columns that no longer exist.
- `AddVehicle_完整必填字段新增成功且可查询到` — replaced by `AddTune_合法_插入成功且IsManual为1且SourceIdentity为空`.
- 5 x `AddVehicle_缺少X` validation tests → replaced by 2 x `EnsureVehicleModel_缺少X` + 5 x `AddTune_缺少X`.

**`SeedVehicle` helper rewritten:**
Now calls `EnsureVehicleModel` + conditionally `AddTune` (only when `Class` and `MatchType` are non-empty), returns `VehicleId`. All downstream tests that used the old `AddVehicle`-backed `SeedVehicle` now work with the new schema.

### Unit tests (new, `CandyBotServiceTests.cs`)

- `HandleForzaHorizonInput_新增车型_非管理员被拒绝` — asserts handled=true, reply="你没有权限执行此操作。"
- `HandleForzaHorizonInput_新增车型_空参数回复用法提示` — asserts handled=true, reply contains "格式：新增车型"
- `HandleForzaHorizonInput_新增调校_非管理员被拒绝`
- `HandleForzaHorizonInput_新增调校_空参数回复用法提示` — asserts reply contains "格式：新增调校"
- `HandleForzaHorizonInput_新增车型前缀先于新增车辆匹配` — asserts "格式：新增车型" not "格式：新增车辆"
- `HandleForzaHorizonInput_新增调校前缀先于新增车辆匹配` — asserts "格式：新增调校" not "格式：新增车辆"
- `HandleForzaHorizonInput_新增车辆兼容别名_非管理员被拒绝`
- `HandleForzaHorizonInput_新增车辆兼容别名_空参数回复包含格式说明`

### Test counts
- Unit tests (`dotnet test tests/CandyBot.Tests`): **113 passed, 0 failed** (was 105 before Task 14)
- Integration tests: require Docker/MySQL; not run in this session. Per Task 13 baseline (15 passed, 21 failed), Task 14 migrations should eliminate the 21 cascade failures coming from `SeedVehicle -> AddVehicle` incompatibility.

## Files Changed

**Commit 3440b71 — `feat(forza): 新增AddTune服务方法（手动创建调校）`:**
- `Services/ForzaService.cs`
  - Added public `AddTune(VehicleTuneRequest request, long actorUserId, out string error) -> int`
  - Validates: `VehicleId` non-null/non-zero; vehicle row exists in `Vehicles`; `Class`/`MatchType`/`Drivetrain` non-empty.
  - Sets `SourceFileId = ""`, `IsManual = true`, `LastUpdatedUserId = actorUserId`.
  - Computes `seq = MAX(SourceIdentitySeq) + 1` in the `(SourceFileId='', SourceIdentity='')` slot to avoid unique key conflicts.
  - Opens transaction, calls `InsertTune`, commits. `Rollback()` in `finally`.

**Commit b4ee177 — `feat(forza): 新增车辆拆为新增车型+新增调校（保留兼容别名）`:**
- `Services/BotMessageHandler/ForzaHorizonInputHandler.cs`
  - Added `新增车型` block before `新增车辆`: parses Manufacturer/Model/Year key-value pairs, validates non-empty, calls `EnsureVehicleModel`. On success replies with VehicleId and a hint to use `新增调校 车辆=<id>`.
  - Added `新增调校` block before `新增车辆`: parses full tune key-value pairs (车辆/等级/比赛类型/驱动 and all optional fields), calls `AddTune`. On success replies with TuneId.
  - Rewrote `新增车辆` as combined alias: parses both model and tune fields, validates Manufacturer+Model+Class+MatchType+Drivetrain non-empty, calls `EnsureVehicleModel` then `AddTune` with the returned VehicleId.
  - Added 3 static parse helpers: `ParseModelKeyValuePairs`, `ParseTuneKeyValuePairs`, `ParseCombinedVehicleKeyValuePairs`.

**Commit b18571e — `test(forza): 迁移AddVehicle测试到新AddTune/EnsureVehicleModel路径`:**
- `tests/CandyBot.IntegrationTests/Forza/ForzaServiceTests.cs`
  - `SeedVehicle` rewritten to use `EnsureVehicleModel` + conditional `AddTune`.
  - Removed 2 old schema-incompatible tests; replaced 5 `AddVehicle_缺少X` tests.
  - Added 2 `EnsureVehicleModel_缺少X`, 5 `AddTune_缺少X`, 2 `AddTune_*` happy-path, and 2 OR/AND SQL probe fixes.

**Commit 56af4f6 — `test(forza): 新增车型/新增调校/新增车辆 指令路由与权限单元测试`:**
- `tests/CandyBot.Tests/Services/CandyBotServiceTests.cs`
  - 8 new handler unit tests covering permission gating, empty-arg usage hints, and prefix ordering invariants for `新增车型` / `新增调校` / `新增车辆`.

## Remaining Red

Integration test baseline from Task 13: **21 failed / 15 passed**.

After Task 14 migrations, the `SeedVehicle -> AddVehicle` cascade is eliminated. Expected new baseline once run against real MySQL:
- Previously-red `AddVehicle`-cascade tests -> now use `EnsureVehicleModel` + `AddTune` path, should be GREEN.
- Tests touching `UpdateVehicle`/`GetVehicleById` may still fail because:
  - `UpdateVehicle` still attempts to UPDATE columns on `Vehicles` that don't exist in the new schema (Task 15 scope).
  - `GetVehicleById(vehicleId)` returns the tune row by `t.Id`; tests passing `SeedVehicle()`'s VehicleId (not TuneId) will get 0 rows (Task 15 scope).
- These are not regressions introduced by Task 14 — they were pre-existing on this branch.

**Estimated remaining red after Task 14 (unverified without Docker):** 3-5 tests in the `UpdateVehicle`/`GetVehicleById` group. Ownership: Task 15.

## Self-Review

**Correctness:**
- `seq` computation: `COALESCE(MAX(SourceIdentitySeq), -1) + 1` starts at 0 on an empty table, increments monotonically. Race condition under concurrent inserts is possible but acceptable for a manually-driven admin command (not hot path). A DB-level UNIQUE constraint on `(SourceFileId, SourceIdentity, SourceIdentitySeq)` will surface the conflict rather than silently corrupt data.
- `forzaConfig.Invalidate()` is NOT called from `AddTune` because it does not change the manufacturer list. Only `EnsureVehicleModel` (which adds to `Vehicles`) needs to invalidate the cache. Correct.
- `ParseTuneKeyValuePairs`: the 车辆 key is correctly parsed as `int?` into `VehicleId`. If parsing fails, returns `error` immediately before the service call.
- Prefix ordering is enforced structurally: `新增车型` and `新增调校` `if` blocks appear before `新增车辆` `if` block in the handler. The prefix ordering unit tests pin this invariant.

**Concerns:**
1. `AddTune` seq computation is not atomic with the INSERT — two concurrent admin calls for the same `(SourceFileId='', SourceIdentity='')` slot could compute the same `seq`, causing a unique key violation. The `catch (Exception)` returns a user-facing error in that case, so data integrity is preserved. Documented as a TODO if this command ever needs high throughput.
2. `新增车辆` alias requires all 5 fields (Manufacturer, Model, Class, MatchType, Drivetrain) to be non-empty before calling either service method. This means if `EnsureVehicleModel` succeeds but the user did not provide tune fields, the whole command fails with a usage hint. This is intentional — the alias is a one-shot combined operation; split operations should use `新增车型` + `新增调校` separately.
3. Handler unit tests exercise only the routing and permission layers (since `forzaService = null!` in `CreateService`, any actual service call throws NullReferenceException caught by the `try/catch` in each handler block, returning `_busy` text). Happy-path service behavior is covered by integration tests only.
