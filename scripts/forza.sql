-- ----------------------------
-- 1. 车辆表
-- ----------------------------
CREATE TABLE `Vehicles` (
    `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '主键',
    `Class` VARCHAR(20) NOT NULL COMMENT '等级',
    `MatchType` VARCHAR(20) NOT NULL COMMENT '比赛类型',
    `Manufacturer` VARCHAR(100) NOT NULL COMMENT '制造商',
    `Model` VARCHAR(100) NOT NULL COMMENT '型号',
    `Year` VARCHAR(20) NULL COMMENT '年份',
    `Drivetrain` VARCHAR(20) NULL COMMENT '传动方式: 四驱, 后驱, 前驱',
    `Attribute` VARCHAR(20) NULL COMMENT '属性: 均衡, 操控, 动力, 高速, 加速等',
    `TireCompound` VARCHAR(50) NULL COMMENT '胎面: 半热熔, 全热熔, 拉力胎, 直线胎, 越野胎, 漂移胎等',
    `SurfaceCondition` VARCHAR(20) NULL COMMENT '适用干/湿地: 干, 湿地, 通用',
    `Strength` VARCHAR(20) NULL COMMENT '强度评级',
    `Tune` VARCHAR(255) NULL COMMENT '调校信息 (调校师XboxID + 关键词)，可存储多个，用逗号分隔',
    `Remarks` VARCHAR(255) NULL COMMENT '备注：手感、特殊开法等',
    `LapTime` VARCHAR(255) NULL COMMENT '圈速测试结果，如“白川54.5”',
    `ExtData` TEXT NULL COMMENT '扩展字段，存储额外属性，如侧向G值、极速等',
    `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
    `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '更新时间',
    PRIMARY KEY (`Id`),
    INDEX `idx_class_match` (`Class`, `MatchType`),
    INDEX `idx_manufacturer_model` (`Manufacturer`, `Model`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='车辆调校表';

-- ----------------------------
-- 2. 赛道表
-- ----------------------------
CREATE TABLE IF NOT EXISTS `Maps` (
    `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '主键',
    `NameZh` VARCHAR(100) NOT NULL COMMENT '中文名',
    `NameEn` VARCHAR(150) NOT NULL COMMENT '英文名',
    `Aliases` JSON NULL COMMENT '别名（字符串数组）',
    `LengthKm` DECIMAL(8,3) NOT NULL COMMENT '赛道长度（公里）',
    `RouteType` VARCHAR(20) NOT NULL DEFAULT 'circuit' COMMENT '路线类型：circuit=圈图, sprint=冲刺图',
    `Surface` VARCHAR(20) NULL COMMENT '路面类型：asphalt/dirt/gravel/sand/mixed/snow/cross_country',
    `RaceCategory` VARCHAR(20) NOT NULL DEFAULT 'road' COMMENT '赛事大类：road/dirt/cross_country/street/drag/rally/showcase/mountain_road',
    `Characteristic` VARCHAR(20) NULL COMMENT '赛道特性：handling/speed/balanced/offroad/drift/technical',
    `Difficulty` TINYINT UNSIGNED NULL COMMENT '难度 1-5（1=新手, 5=专家）',
    `Elevation` VARCHAR(20) NULL COMMENT '海拔变化：flat/gentle/hilly/mountainous',
    `TurnCount` SMALLINT UNSIGNED NULL COMMENT '弯道数量（估算）',
    `RoadWidth` VARCHAR(20) NULL COMMENT '道路宽度：narrow/normal/wide',
    `AvgTimeSec` INT UNSIGNED NULL COMMENT '标准圈速/完赛时间（秒）',
    `WorldRecord` VARCHAR(100) NOT NULL DEFAULT '' COMMENT '世界纪录时间（字符串，如 01:23.456）',
    `WrVehicle` VARCHAR(200) NOT NULL DEFAULT '' COMMENT '世界纪录所用车辆（品牌+型号）',
    `WrTuning` VARCHAR(500) NOT NULL DEFAULT '' COMMENT '世界纪录车辆调校参数',
    `CarClassBest` JSON NULL COMMENT '推荐车辆等级，如 ["S1","S2"]',
    `CarTypeBest` JSON NULL COMMENT '推荐车辆类型，如 ["hypercar","rally"]',
    `Region` VARCHAR(80) NULL COMMENT '所在区域/地名',
    `SeasonBest` VARCHAR(20) NULL COMMENT '最佳赛季：spring/summer/autumn/winter/wet/dry/all',
    `WeatherDefault` VARCHAR(20) NULL COMMENT '默认天气：clear/rain/fog/storm/snow/variable',
    `NightMode` TINYINT(1) NOT NULL DEFAULT 0 COMMENT '是否有夜间版本（0=否, 1=是）',
    `Dlc` VARCHAR(100) NULL COMMENT '所属DLC/扩展包名称',
    `IsReverse` TINYINT(1) NOT NULL DEFAULT 0 COMMENT '是否为反向赛道（0=正向, 1=反向）',
    `ImageUrl` VARCHAR(500) NULL COMMENT '赛道预览图URL',
    `MinimapUrl` VARCHAR(500) NULL COMMENT '小地图URL',
    `Notes` TEXT NULL COMMENT '备注',
    `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
    `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '更新时间',
    PRIMARY KEY (`Id`),
    KEY `idx_maps_name_zh` (`NameZh`),
    KEY `idx_maps_name_en` (`NameEn`),
    KEY `idx_maps_route_type` (`RouteType`),
    KEY `idx_maps_surface` (`Surface`),
    KEY `idx_maps_race_category` (`RaceCategory`),
    KEY `idx_maps_characteristic` (`Characteristic`),
    KEY `idx_maps_difficulty` (`Difficulty`),
    KEY `idx_maps_region` (`Region`),
    KEY `idx_maps_dlc` (`Dlc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='赛道表';

-- ----------------------------
-- 3. 赛道组表
-- ----------------------------
CREATE TABLE IF NOT EXISTS `MapGroups` (
    `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '主键',
    `NameZh` VARCHAR(100) NOT NULL COMMENT '中文组名',
    `NameEn` VARCHAR(150) NULL COMMENT '英文组名',
    `Description` TEXT NULL COMMENT '组描述（推荐理由等）',
    `GroupType` VARCHAR(20) NOT NULL DEFAULT 'custom' COMMENT '组类型：championship/car_test/theme/custom',
    `RecommendedCarClass` JSON NULL COMMENT '推荐车辆等级',
    `RecommendedCarStyle` VARCHAR(20) NOT NULL DEFAULT 'all' COMMENT '适配赛车风格：hypercar/supercar/sports/muscle/rally/offroad/classic/tuner/truck/buggy/track_toy/all',
    `SeasonPreferred` VARCHAR(20) NOT NULL DEFAULT 'all' COMMENT '适配赛季：spring/summer/autumn/winter/wet/dry/all',
    `WeatherPreferred` VARCHAR(20) NOT NULL DEFAULT 'all' COMMENT '适配天气：clear/rain/fog/storm/snow/variable/all',
    `Tags` JSON NULL COMMENT '标签数组',
    `Notes` TEXT NULL COMMENT '备注',
    `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
    `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '更新时间',
    PRIMARY KEY (`Id`),
    KEY `idx_map_groups_group_type` (`GroupType`),
    KEY `idx_map_groups_car_style` (`RecommendedCarStyle`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='赛道组表';

-- ----------------------------
-- 4. 赛道-赛道组关联表
-- ----------------------------
CREATE TABLE IF NOT EXISTS `MapGroupMaps` (
    `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '关联ID',
    `GroupId` INT UNSIGNED NOT NULL COMMENT '赛道组ID',
    `MapId` INT UNSIGNED NOT NULL COMMENT '赛道ID',
    `SortOrder` TINYINT UNSIGNED NOT NULL DEFAULT 0 COMMENT '组内排序',
    PRIMARY KEY (`Id`),
    UNIQUE KEY `uk_map_group_map` (`GroupId`, `MapId`),
    KEY `idx_mgm_group_id` (`GroupId`),
    KEY `idx_mgm_map_id` (`MapId`),
    CONSTRAINT `fk_mgm_group` FOREIGN KEY (`GroupId`) REFERENCES `MapGroups` (`Id`) ON DELETE CASCADE ON UPDATE CASCADE,
    CONSTRAINT `fk_mgm_map` FOREIGN KEY (`MapId`) REFERENCES `Maps` (`Id`) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='赛道与赛道组关联表';
