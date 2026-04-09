-- 创建任务缓存表
-- 用于存储因点位被锁定而无法立即执行的任务

CREATE TABLE [dbo].[RCS_TaskCache] (
    [Id]                    INT IDENTITY(1,1)   NOT NULL,
    [TaskType]              INT                 NOT NULL,           -- 任务类型
    [SourcePosition]        NVARCHAR(100)       NOT NULL,           -- 起点
    [TargetPosition]        NVARCHAR(100)       NOT NULL,           -- 终点
    [MaterialCode]          NVARCHAR(100)       NULL,               -- 物料编码
    [MaterialQuantity]      INT                 NOT NULL DEFAULT 0, -- 物料数量
    [PalletNumber]          NVARCHAR(100)       NULL,               -- 卡板单号
    [SourceType]            NVARCHAR(50)        NULL,               -- 起始类型
    [DestType]              NVARCHAR(50)        NULL,               -- 目标类型
    [Priority]              INT                 NOT NULL DEFAULT 1, -- 优先级
    [RequestCode]           NVARCHAR(100)       NOT NULL,           -- 请求编号
    [CreateTime]            DATETIME            NOT NULL DEFAULT GETDATE(), -- 创建时间
    [ConfirmTime]           DATETIME            NULL,               -- 确认时间
    [RetryCount]            INT                 NOT NULL DEFAULT 0, -- 重试次数
    [LastError]             NVARCHAR(500)       NULL,               -- 最后错误信息
    [Status]                INT                 NOT NULL DEFAULT 0, -- 状态：0-待处理，1-处理中，2-已完成，3-已取消
    [TaskGroupId]           NVARCHAR(50)        NULL,               -- 任务分组ID（用于拆分任务）
    [TaskSequence]          INT                 NOT NULL DEFAULT 1, -- 任务序号
    [IsSplitTask]           BIT                 NOT NULL DEFAULT 0, -- 是否为拆分任务
    [OriginalTaskId]        INT                 NULL,               -- 原始任务ID
    CONSTRAINT [PK_RCS_TaskCache] PRIMARY KEY CLUSTERED ([Id] ASC)
);

-- 创建索引以提高查询性能
CREATE NONCLUSTERED INDEX [IX_RCS_TaskCache_CreateTime] ON [dbo].[RCS_TaskCache] ([CreateTime] DESC);
CREATE NONCLUSTERED INDEX [IX_RCS_TaskCache_Status] ON [dbo].[RCS_TaskCache] ([Status]);
CREATE NONCLUSTERED INDEX [IX_RCS_TaskCache_TaskGroupId] ON [dbo].[RCS_TaskCache] ([TaskGroupId]);
CREATE NONCLUSTERED INDEX [IX_RCS_TaskCache_SourcePosition] ON [dbo].[RCS_TaskCache] ([SourcePosition]);
CREATE NONCLUSTERED INDEX [IX_RCS_TaskCache_TargetPosition] ON [dbo].[RCS_TaskCache] ([TargetPosition]);

PRINT 'RCS_TaskCache表创建完成！';