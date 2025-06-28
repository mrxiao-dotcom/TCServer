CREATE TABLE `account_balances` (
	`id` BIGINT(19) NOT NULL AUTO_INCREMENT,
	`account_id` BIGINT(19) NOT NULL COMMENT '关联的账户ID',
	`total_equity` DECIMAL(20,8) NOT NULL COMMENT '账户总权益',
	`available_balance` DECIMAL(20,8) NOT NULL COMMENT '可用余额',
	`margin_balance` DECIMAL(20,8) NOT NULL COMMENT '保证金余额',
	`unrealized_pnl` DECIMAL(20,8) NOT NULL COMMENT '未实现盈亏',
	`timestamp` DATETIME NOT NULL COMMENT '数据更新时间',
	`created_at` DATETIME NOT NULL DEFAULT 'CURRENT_TIMESTAMP',
	`updated_at` DATETIME NOT NULL DEFAULT 'CURRENT_TIMESTAMP' ON UPDATE CURRENT_TIMESTAMP,
	PRIMARY KEY (`id`) USING BTREE,
	INDEX `idx_account_timestamp` (`account_id`, `timestamp`) USING BTREE,
	CONSTRAINT `account_balances_ibfk_1` FOREIGN KEY (`account_id`) REFERENCES `trading_accounts` (`id`) ON UPDATE NO ACTION ON DELETE NO ACTION
)
COMMENT='账户余额记录表'
COLLATE='utf8mb4_0900_ai_ci'
ENGINE=InnoDB
AUTO_INCREMENT=47
;

CREATE TABLE `account_positions` (
	`id` BIGINT(19) NOT NULL AUTO_INCREMENT,
	`account_id` BIGINT(19) NOT NULL,
	`symbol` VARCHAR(20) NOT NULL COMMENT '合约名称' COLLATE 'utf8mb4_0900_ai_ci',
	`position_side` ENUM('LONG','SHORT') NOT NULL COMMENT '持仓方向' COLLATE 'utf8mb4_0900_ai_ci',
	`entry_price` DECIMAL(20,8) NOT NULL COMMENT '开仓均价',
	`mark_price` DECIMAL(20,8) NOT NULL COMMENT '标记价格',
	`position_amt` DECIMAL(20,8) NOT NULL COMMENT '持仓数量',
	`leverage` INT(10) NOT NULL COMMENT '杠杆倍数',
	`margin_type` ENUM('ISOLATED','CROSS') NOT NULL COMMENT '保证金类型' COLLATE 'utf8mb4_0900_ai_ci',
	`isolated_margin` DECIMAL(20,8) NULL DEFAULT NULL COMMENT '逐仓保证金',
	`unrealized_pnl` DECIMAL(20,8) NOT NULL COMMENT '未实现盈亏',
	`record_date` DATE NOT NULL,
	`liquidation_price` DECIMAL(20,8) NULL DEFAULT NULL COMMENT '强平价格',
	`timestamp` DATETIME NOT NULL COMMENT '数据更新时间',
	`created_at` DATETIME NOT NULL DEFAULT 'CURRENT_TIMESTAMP',
	`updated_at` DATETIME NOT NULL DEFAULT 'CURRENT_TIMESTAMP' ON UPDATE CURRENT_TIMESTAMP,
	PRIMARY KEY (`id`) USING BTREE,
	UNIQUE INDEX `idx_account_positions_account_symbol_date` (`account_id`, `symbol`, `record_date`) USING BTREE,
	INDEX `idx_account_symbol` (`account_id`, `symbol`) USING BTREE,
	INDEX `idx_account_timestamp` (`account_id`, `timestamp`) USING BTREE,
	INDEX `idx_account_positions_account_id` (`account_id`) USING BTREE,
	INDEX `idx_account_positions_record_date` (`record_date`) USING BTREE,
	CONSTRAINT `account_positions_ibfk_1` FOREIGN KEY (`account_id`) REFERENCES `trading_accounts` (`id`) ON UPDATE NO ACTION ON DELETE NO ACTION
)
COMMENT='账户持仓记录表'
COLLATE='utf8mb4_0900_ai_ci'
ENGINE=InnoDB
AUTO_INCREMENT=4368
;

CREATE TABLE `account_equity_history` (
	`id` BIGINT(19) NOT NULL AUTO_INCREMENT,
	`account_id` VARCHAR(50) NOT NULL COMMENT '账户ID' COLLATE 'utf8mb4_0900_ai_ci',
	`equity` DECIMAL(20,8) NOT NULL DEFAULT '0.00000000' COMMENT '账户权益',
	`available` DECIMAL(20,8) NOT NULL DEFAULT '0.00000000' COMMENT '可用余额',
	`position_value` DECIMAL(20,8) NOT NULL DEFAULT '0.00000000' COMMENT '持仓市值',
	`leverage` DECIMAL(10,2) NOT NULL DEFAULT '0.00' COMMENT '杠杆倍数',
	`long_value` DECIMAL(20,8) NOT NULL DEFAULT '0.00000000' COMMENT '多头市值',
	`short_value` DECIMAL(20,8) NOT NULL DEFAULT '0.00000000' COMMENT '空头市值',
	`long_count` INT(10) NOT NULL DEFAULT '0' COMMENT '多头持仓数',
	`short_count` INT(10) NOT NULL DEFAULT '0' COMMENT '空头持仓数',
	`create_time` DATETIME NOT NULL DEFAULT 'CURRENT_TIMESTAMP' COMMENT '创建时间',
	PRIMARY KEY (`id`) USING BTREE,
	INDEX `idx_account_time` (`account_id`, `create_time`) USING BTREE
)
COMMENT='账户权益历史'
COLLATE='utf8mb4_0900_ai_ci'
ENGINE=InnoDB
AUTO_INCREMENT=1179178
;

CREATE TABLE `acct_info` (
	`acct_id` INT(10) NOT NULL AUTO_INCREMENT,
	`acct_name` VARCHAR(255) NULL DEFAULT NULL COLLATE 'gb2312_chinese_ci',
	`acct_date` DATETIME NULL DEFAULT NULL,
	`memo` VARCHAR(255) NULL DEFAULT NULL COMMENT '显示名字' COLLATE 'gb2312_chinese_ci',
	`apikey` VARCHAR(255) NULL DEFAULT NULL COLLATE 'gb2312_chinese_ci',
	`secretkey` VARCHAR(255) NULL DEFAULT NULL COLLATE 'gb2312_chinese_ci',
	`apipass` VARCHAR(255) NULL DEFAULT NULL COLLATE 'gb2312_chinese_ci',
	`state` INT(10) NULL DEFAULT NULL,
	`status` INT(10) NULL DEFAULT NULL,
	`email` VARCHAR(255) NULL DEFAULT NULL COLLATE 'gb2312_chinese_ci',
	`group_id` INT(10) NULL DEFAULT NULL,
	`sendflag` INT(10) NULL DEFAULT NULL,
	PRIMARY KEY (`acct_id`) USING BTREE
)
COLLATE='gb2312_chinese_ci'
ENGINE=InnoDB
AUTO_INCREMENT=10001
;
