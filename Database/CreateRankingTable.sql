-- 创建每日涨跌幅排名表
CREATE TABLE IF NOT EXISTS `daily_ranking` (
  `id` int NOT NULL AUTO_INCREMENT,
  `date` date NOT NULL COMMENT '日期',
  `top_gainers` text NOT NULL COMMENT '涨幅前十（格式：1#合约名#涨幅|2#合约名#涨幅|...）',
  `top_losers` text NOT NULL COMMENT '跌幅前十（格式：1#合约名#跌幅|2#合约名#跌幅|...）',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '更新时间',
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_date` (`date`) COMMENT '确保每天只有一条记录'
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='每日合约涨跌幅排名表';

-- 添加连接池配置
SET GLOBAL max_connections = 1000;
SET GLOBAL wait_timeout = 28800;
SET GLOBAL interactive_timeout = 28800; 