-- 修复外键约束兼容性问题
-- 原因：account_balances.account_id (BIGINT) 与 acct_info.acct_id (INT) 数据类型不匹配

-- 步骤1：首先删除现有的外键约束（如果存在）
SET FOREIGN_KEY_CHECKS = 0;
ALTER TABLE account_balances DROP FOREIGN KEY IF EXISTS account_balances_ibfk_1;
ALTER TABLE account_positions DROP FOREIGN KEY IF EXISTS account_positions_ibfk_1;
SET FOREIGN_KEY_CHECKS = 1;

-- 步骤2：修改列的数据类型使其匹配
-- 将 account_balances.account_id 从 BIGINT 改为 INT
ALTER TABLE account_balances MODIFY COLUMN account_id INT(10) NOT NULL;

-- 将 account_positions.account_id 从 BIGINT 改为 INT  
ALTER TABLE account_positions MODIFY COLUMN account_id INT(10) NOT NULL;

-- 步骤3：重新创建外键约束
ALTER TABLE account_balances 
ADD CONSTRAINT account_balances_ibfk_1 
FOREIGN KEY (account_id) REFERENCES acct_info(acct_id) 
ON UPDATE NO ACTION ON DELETE CASCADE;

ALTER TABLE account_positions 
ADD CONSTRAINT account_positions_ibfk_1 
FOREIGN KEY (account_id) REFERENCES acct_info(acct_id) 
ON UPDATE NO ACTION ON DELETE CASCADE;

-- 验证外键约束是否创建成功
SHOW CREATE TABLE account_balances;
SHOW CREATE TABLE account_positions; 