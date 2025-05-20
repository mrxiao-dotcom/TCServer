-- 创建K线数据表
CREATE TABLE `kline_data` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `symbol` varchar(20) NOT NULL COMMENT '交易对符号',
  `open_time` datetime NOT NULL COMMENT '开盘时间',
  `open_price` decimal(20,8) NOT NULL COMMENT '开盘价',
  `high_price` decimal(20,8) NOT NULL COMMENT '最高价',
  `low_price` decimal(20,8) NOT NULL COMMENT '最低价',
  `close_price` decimal(20,8) NOT NULL COMMENT '收盘价',
  `volume` decimal(30,8) NOT NULL COMMENT '成交量',
  `close_time` datetime NOT NULL COMMENT '收盘时间',
  `quote_volume` decimal(30,8) NOT NULL COMMENT '成交额',
  `trades` int NOT NULL COMMENT '成交笔数',
  `taker_buy_volume` decimal(30,8) NOT NULL COMMENT '主动买入成交量',
  `taker_buy_quote_volume` decimal(30,8) NOT NULL COMMENT '主动买入成交额',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_symbol_time` (`symbol`,`open_time`),
  KEY `idx_symbol` (`symbol`),
  KEY `idx_open_time` (`open_time`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='K线数据表';

-- 创建系统配置表
CREATE TABLE `system_config` (
  `id` int NOT NULL AUTO_INCREMENT,
  `config_key` varchar(50) NOT NULL COMMENT '配置键',
  `config_value` varchar(500) NOT NULL COMMENT '配置值',
  `description` varchar(200) DEFAULT NULL COMMENT '描述',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_config_key` (`config_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='系统配置表';

-- 插入默认配置
INSERT INTO `system_config` (`config_key`, `config_value`, `description`) VALUES
('KlineFetchTime', '00:05:00', 'K线数据获取时间'),
('BatchSize', '10', '每批次获取的交易对数量'); 