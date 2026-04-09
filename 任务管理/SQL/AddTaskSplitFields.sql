-- 为RCS_UserTasks表添加任务拆分相关字段
-- 执行前请备份数据库

-- 检查字段是否存在，如果不存在则添加
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'RCS_UserTasks' AND COLUMN_NAME = 'TaskGroupId')
BEGIN
    ALTER TABLE RCS_UserTasks ADD TaskGroupId NVARCHAR(50) NULL;
    PRINT '已添加字段 TaskGroupId';
END

IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'RCS_UserTasks' AND COLUMN_NAME = 'TaskSequence')
BEGIN
    ALTER TABLE RCS_UserTasks ADD TaskSequence INT NOT NULL DEFAULT 1;
    PRINT '已添加字段 TaskSequence';
END

IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'RCS_UserTasks' AND COLUMN_NAME = 'IsSplitTask')
BEGIN
    ALTER TABLE RCS_UserTasks ADD IsSplitTask BIT NOT NULL DEFAULT 0;
    PRINT '已添加字段 IsSplitTask';
END

IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'RCS_UserTasks' AND COLUMN_NAME = 'OriginalTaskId')
BEGIN
    ALTER TABLE RCS_UserTasks ADD OriginalTaskId INT NULL;
    PRINT '已添加字段 OriginalTaskId';
END

-- 为TaskGroupId字段添加索引以提高查询性能
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_RCS_UserTasks_TaskGroupId' AND object_id = OBJECT_ID('RCS_UserTasks'))
BEGIN
    CREATE INDEX IX_RCS_UserTasks_TaskGroupId ON RCS_UserTasks (TaskGroupId);
    PRINT '已创建索引 IX_RCS_UserTasks_TaskGroupId';
END

-- 为IsSplitTask字段添加索引
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_RCS_UserTasks_IsSplitTask' AND object_id = OBJECT_ID('RCS_UserTasks'))
BEGIN
    CREATE INDEX IX_RCS_UserTasks_IsSplitTask ON RCS_UserTasks (IsSplitTask);
    PRINT '已创建索引 IX_RCS_UserTasks_IsSplitTask';
END

-- 为OriginalTaskId字段添加外键约束（可选）
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_RCS_UserTasks_OriginalTaskId')
BEGIN
    ALTER TABLE RCS_UserTasks 
    ADD CONSTRAINT FK_RCS_UserTasks_OriginalTaskId 
    FOREIGN KEY (OriginalTaskId) REFERENCES RCS_UserTasks(ID);
    PRINT '已创建外键约束 FK_RCS_UserTasks_OriginalTaskId';
END

PRINT 'RCS_UserTasks表任务拆分字段更新完成！'
