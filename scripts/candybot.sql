-- 昵称审核系统
DROP TABLE IF EXISTS `NicknameChangeRequests`;
CREATE TABLE `NicknameChangeRequests` (
  `Id` bigint(20) NOT NULL AUTO_INCREMENT,
  `UserId` bigint(20) NOT NULL DEFAULT '0' COMMENT '用户ID',
  `OldName` varchar(255) NOT NULL DEFAULT '' COMMENT '旧昵称',
  `NewName` varchar(255) NOT NULL DEFAULT '' COMMENT '申请的新昵称',
  `SubmitTime` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '提交时间',
  `SubmitCount` int(11) NOT NULL DEFAULT '1' COMMENT '提交计数',
  `Status` int(1) NOT NULL DEFAULT '0' COMMENT '0=进行中，1=通过，2=拒绝，3=用户取消',
  `ReviewerId` bigint(20) NOT NULL DEFAULT '0' COMMENT '审核员ID',
  `ReviewTime` datetime DEFAULT NULL COMMENT '审核时间',
  `RejectReason` varchar(255) NOT NULL DEFAULT '' COMMENT '拒绝原因',
  `LastUpdateTime` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '最后更新时间',
  PRIMARY KEY (`Id`),
  KEY `idx_userid_status` (`UserId`, `Status`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='昵称修改审核表';
