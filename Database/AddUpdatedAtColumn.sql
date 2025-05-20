-- 添加updated_at字段到kline_data表
ALTER TABLE `kline_data`
ADD COLUMN `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP AFTER `created_at`; 