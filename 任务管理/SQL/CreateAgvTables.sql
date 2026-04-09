-- 创建AGV信息表
CREATE TABLE [dbo].[AGV_Info] (
    [Id]                 INT             IDENTITY (1, 1) NOT NULL,
    [AgvId]              NVARCHAR (50)   NOT NULL,
    [AgvName]            NVARCHAR (100)  NULL,
    [AgvType]            NVARCHAR (50)   NULL,
    [Status]             INT             DEFAULT ((0)) NOT NULL,
    [X]                  FLOAT           DEFAULT ((0)) NOT NULL,
    [Y]                  FLOAT           DEFAULT ((0)) NOT NULL,
    [Angle]              FLOAT           DEFAULT ((0)) NOT NULL,
    [BatteryLevel]       FLOAT           DEFAULT ((0)) NOT NULL,
    [BatteryTemp]        FLOAT           DEFAULT ((0)) NOT NULL,
    [ChargingStatus]     INT             DEFAULT ((0)) NOT NULL,
    [JackStatus]         INT             DEFAULT ((0)) NOT NULL,
    [LoadStatus]         INT             DEFAULT ((0)) NOT NULL,
    [TotalRunTime]       FLOAT           DEFAULT ((0)) NOT NULL,
    [TotalDistance]      FLOAT           DEFAULT ((0)) NOT NULL,
    [ChargeCount]        INT             DEFAULT ((0)) NOT NULL,
    [BatteryCircleCount] FLOAT           DEFAULT ((0)) NOT NULL,
    [LastUpdateTime]     DATETIME        DEFAULT (getdate()) NOT NULL,
    [Remarks]            NVARCHAR (500)  NULL,
    CONSTRAINT [PK_AGV_Info] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [UK_AGV_Info_AgvId] UNIQUE NONCLUSTERED ([AgvId] ASC)
);

-- 创建AGV报警信息表
CREATE TABLE [dbo].[AGV_Alarm] (
    [Id]             INT             IDENTITY (1, 1) NOT NULL,
    [AgvId]          NVARCHAR (50)   NOT NULL,
    [AlarmCode]      NVARCHAR (50)   NOT NULL,
    [AlarmContent]   NVARCHAR (500)  NULL,
    [AlarmLevel]     INT             DEFAULT ((1)) NOT NULL,
    [AlarmTime]      DATETIME        DEFAULT (getdate()) NOT NULL,
    [ProcessStatus]  INT             DEFAULT ((0)) NOT NULL,
    [ProcessTime]    DATETIME        NULL,
    [Processor]      NVARCHAR (50)   NULL,
    [ProcessRemarks] NVARCHAR (500)  NULL,
    CONSTRAINT [PK_AGV_Alarm] PRIMARY KEY CLUSTERED ([Id] ASC)
);

-- 创建索引
CREATE NONCLUSTERED INDEX [IX_AGV_Alarm_AgvId] ON [dbo].[AGV_Alarm] ([AgvId] ASC);
CREATE NONCLUSTERED INDEX [IX_AGV_Alarm_AlarmTime] ON [dbo].[AGV_Alarm] ([AlarmTime] DESC);
CREATE NONCLUSTERED INDEX [IX_AGV_Alarm_ProcessStatus] ON [dbo].[AGV_Alarm] ([ProcessStatus] ASC);

-- 添加外键约束
ALTER TABLE [dbo].[AGV_Alarm] WITH CHECK ADD CONSTRAINT [FK_AGV_Alarm_AGV_Info] 
    FOREIGN KEY([AgvId]) REFERENCES [dbo].[AGV_Info] ([AgvId]);

-- 添加测试数据
INSERT INTO [dbo].[AGV_Info] 
    ([AgvId], [AgvName], [AgvType], [Status], [X], [Y], [Angle], 
    [BatteryLevel], [BatteryTemp], [ChargingStatus], [JackStatus], [LoadStatus], 
    [TotalRunTime], [TotalDistance], [ChargeCount], [BatteryCircleCount])
VALUES
    ('AGV01', 'AGV-01', 'ForkLift', 1, 49.73, 42.64, 3.14, 
    0.39, 32.0, 1, 1, 0, 
    19325.74, 110457.27, 2752, 1271.0),
    ('AGV02', 'AGV-02', 'ForkLift', 1, 35.42, 78.91, 1.57, 
    0.65, 30.5, 0, 0, 1, 
    15642.32, 98765.43, 2105, 1023.5),
    ('AGV03', 'AGV-03', 'ForkLift', 0, 0.0, 0.0, 0.0, 
    0.0, 0.0, 0, 0, 0, 
    12345.67, 87654.32, 1876, 987.5),
    ('AGV04', 'AGV-04', 'ForkLift', 1, 87.65, 12.34, 0.78, 
    0.82, 31.2, 0, 0, 0, 
    9876.54, 65432.10, 1543, 765.2);

-- 添加报警测试数据
INSERT INTO [dbo].[AGV_Alarm]
    ([AgvId], [AlarmCode], [AlarmContent], [AlarmLevel], [AlarmTime], [ProcessStatus])
VALUES
    ('AGV01', '010', '导航失败', 3, DATEADD(MINUTE, -30, GETDATE()), 0),
    ('AGV01', '020', '电量低', 2, DATEADD(MINUTE, -25, GETDATE()), 0),
    ('AGV02', '030', '通信中断', 3, DATEADD(MINUTE, -15, GETDATE()), 0),
    ('AGV02', '040', '障碍物检测', 2, DATEADD(MINUTE, -10, GETDATE()), 1),
    ('AGV04', '050', '任务超时', 2, DATEADD(MINUTE, -5, GETDATE()), 0); 