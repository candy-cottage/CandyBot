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
