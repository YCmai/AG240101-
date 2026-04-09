# 仓库管理系统 API 文档

## 更新说明

### 2024年最新更新 - RCS_UserTasks实体字段扩展

为了支持更详细的任务管理功能，RCS_UserTasks实体新增了以下字段：

| 字段名 | 中文描述 | 类型 | 说明 |
| ------ | -------- | ---- | ---- |
| MaterialCode | 物料编码 | String | 任务相关的物料编码 |
| MaterialQuantity | 物料数量 | int | 任务相关的物料数量 |
| PalletNumber | 卡板单号 | String | 任务相关的卡板单号 |
| SourceType | 起始类型 | String | 起始位置的类型标识 |
| DestType | 目标类型 | String | 目标位置的类型标识 |
| ConfirmTime | 确认时间 | DateTime | 任务确认的时间 |

这些字段已在以下功能中集成：
- 任务列表页面显示
- 任务创建表单
- 任务导出功能
- API接口支持

### 数据库更新

请执行 `SQL/UpdateRcsUserTasksTable.sql` 脚本来更新数据库表结构。

## 任务管理接口

### 添加任务

**功能描述**：添加新的任务到系统

**接口方式**：POST

**接口地址**：http://[服务器地址]:5002/api/rcs/addTask

#### 请求参数

| 参数名 | 中文描述 | 类型 | 参数示例 | 参数范围 |
| ------ | -------- | ---- | -------- | -------- |
| toNum | 任务编码 | String | "5780280" | 不重复字符串 |
| suNum | 卡板单号 | String | "10000141577" | 不重复字符串 |
| material | 物料编码 | String | "9221080" | 可为空 |
| materialNum | 物料数量 | int | 11050 | 可为空 |
| createTime | 创建时间 | String | "2025-05-05T14:48:00Z" | 不可为空 |
| confirmTime | 确认时间 | String | "2023-05-05T14:49:00Z" | 可为空 |
| sourceType | 起始类型 | String | "001" | 详见"任务类型"-示例 |
| sourceBin | 起始位置 | String | "01A01" | 详见"Bin 信息"-示例 |
| destType | 目标类型 | String | "004" | 详见"任务类型"-示例 |
| taskType | 任务类型 | String | "binToBin" | 详见"任务类型"-示例 |
| destBin | 目标位置 | String | "P099-01" | 详见"Bin 信息"-示例 |

#### 返回参数

| 参数名 | 中文描述 | 类型 | 参数示例 | 参数范围 |
| ------ | -------- | ---- | -------- | -------- |
| toNum | 任务编码 | String | "5780280" | 不重复字符串 |
| callTime | 调用时间 | String | "2023-05-05T14:49:00Z" | - |
| status | 返回状态 | string | "Success" | "Success"、"Fail" |
| failType | 失败类型 | int | null | 根据错误类型返回相应代码 |
| message | 结果提示 | string | "" | - |

#### 失败类型说明

| 代码 | 说明 |
| ---- | ---- |
| 101 | 工位不存在 |
| 102 | 工位已占用 |
| 103 | 起始位置为空 |
| 104 | 路径不存在 |
| 105 | 参数错误 |

#### 成功示例

```json
{
  "toNum": "5780280",
  "callTime": "2023-05-05T14:49:00Z",
  "status": "Success",
  "failType": null,
  "message": "任务下发成功"
}
```

#### 失败示例

```json
{
  "toNum": "5780280",
  "callTime": "2023-05-05T14:49:00Z",
  "status": "Fail",
  "failType": 101,
  "message": "任务下发失败,工位不存在"
}
```

### 任务状态回传

**功能描述**：更新任务状态

**接口方式**：POST

**接口地址**：http://[服务器地址]:5002/api/rcs/updateTaskStatus

#### 请求参数

| 参数名 | 中文描述 | 类型 | 参数示例 | 参数范围 |
| ------ | -------- | ---- | -------- | -------- |
| toNum | 任务编码 | String | "5780280" | 不重复字符串 |
| status | 状态 | String | "0" | 0: 初始化, 1: 已接收, 2: 已接盘, 3: 任务完成, 4: 任务取消 |

#### 返回参数

| 参数名 | 中文描述 | 类型 | 参数示例 | 参数范围 |
| ------ | -------- | ---- | -------- | -------- |
| status | 返回状态 | string | "Success" | "Success"、"Fail" |
| message | 结果提示 | string | "" | - |

#### 成功示例

```json
{
  "status": "Success",
  "message": ""
}
```

#### 失败示例

```json
{
  "status": "Fail",
  "message": ""
}
```

### 取消任务

**功能描述**：取消指定任务

**接口方式**：POST

**接口地址**：http://[服务器地址]:5002/api/rcs/cancelTask

#### 请求参数

| 参数名 | 中文描述 | 类型 | 参数示例 | 参数范围 |
| ------ | -------- | ---- | -------- | -------- |
| toNum | 任务编码 | String | "5780280" | 不重复字符串 |

#### 返回参数

| 参数名 | 中文描述 | 类型 | 参数示例 | 参数范围 |
| ------ | -------- | ---- | -------- | -------- |
| status | 返回状态 | string | "Success" | "Success"、"Fail" |
| message | 结果提示 | string | "" | - |

#### 成功示例

```json
{
  "status": "Success",
  "message": "任务取消成功"
}
```

#### 失败示例

```json
{
  "status": "Fail",
  "message": "任务取消失败,任务不存在"
}
```

### 任务清单

**功能描述**：获取任务清单（最近一个月，31天）

**接口方式**：GET

**接口地址**：http://[服务器地址]:5002/api/rcs/getTaskList

#### 请求参数

| 参数名 | 中文描述 | 类型 | 参数示例 | 参数范围 |
| ------ | -------- | ---- | -------- | -------- |
| startTime | 开始时间 | DateTime | "2023-05-01T00:00:00" | 可选参数 |
| endTime | 结束时间 | DateTime | "2023-05-31T23:59:59" | 可选参数 |
| pageIndex | 页码 | int | 1 | 默认1 |
| pageSize | 每页记录数 | int | 20 | 默认20 |

#### 返回参数

| 参数名 | 中文描述 | 类型 | 参数示例 | 参数范围 |
| ------ | -------- | ---- | -------- | -------- |
| status | 返回状态 | string | "Success" | "Success"、"Fail" |
| data | 清单 | list | [] | - |
| toNum | 任务编码 | String | "5780280" | 不重复字符串 |
| suNum | 卡板单号 | String | "10000141577" | 不重复字符串 |
| material | 物料编码 | String | "9221080" | 可为null |
| materialNum | 物料数量 | int | 11050 | 可为null |
| createTime | 创建时间 | String | "2025-05-05T14:48:00Z" | - |
| startTime | 任务开始时间 | String | "2023-05-05T14:49:00Z" | - |
| finishTime | 任务完成时间 | String | "2023-05-05T14:49:00Z" | - |
| sourceType | 起始类型 | String | "001" | 详见"任务类型"-示例 |
| sourceBin | 起始位置 | String | "01A01" | 详见"Bin 信息"-示例 |
| destType | 目标类型 | String | "004" | 详见"任务类型"-示例 |
| destBin | 目标位置 | String | "P099-01" | 详见"Bin 信息"-示例 |
| taskType | 任务类型 | String | "成品出货" | 详见"任务类型"-示例 |
| msg | 信息 | String | "Success" | "Success"、"Fail" |
| step | 步骤 | String | "finish" | "finish"、"下货架"、"上货架"、"中转过程" |
| agvId | 执行任务的车 | list | ["AGV01","AGV03"] | 此任务涉及的车辆 |
| message | 结果提示 | string | "" | - |

#### 成功示例

```json
{
  "status": "Success",
  "data": [{
    "toNum": "5780280",
    "suNum": "10000141577",
    "material": "9221080",
    "materialNum": 11050,
    "createTime": "2025-05-05T14:48:00Z",
    "startTime": "2025-05-05T14:48:00Z",
    "finishTime": "2025-05-05T14:48:00Z",
    "sourceType": "001",
    "sourceBin": "01A01",
    "destType": "004",
    "destBin": "P099-01",
    "taskType": "成品出货",
    "msg": "success",
    "step": "finish",
    "agvId": ["AGV01","AGV03"]
  }],
  "message": "请求成功"
}
```

#### 失败示例

```json
{
  "status": "Fail",
  "data": [],
  "message": "连接失败"
}
```

### 按时间段查询任务

**功能描述**：按时间段查询任务（按创建时间筛选）

**接口方式**：POST

**接口地址**：http://[服务器地址]:5002/api/rcs/timeGetTask

#### 请求参数

| 参数名 | 中文描述 | 类型 | 参数示例 | 参数范围 |
| ------ | -------- | ---- | -------- | -------- |
| createTimeStart | 开始时间 | String | "2025-05-05T14:48:00Z" | 不可为空 |
| createTimeEnd | 结束时间 | String | "2025-05-05T14:48:00Z" | 不可为空 |

#### 返回参数

与任务清单接口返回参数相同

#### 成功示例

```json
{
  "status": "Success",
  "data": [{
    "toNum": "5780280",
    "suNum": "10000141577",
    "material": "9221080",
    "materialNum": 11050,
    "createTime": "2025-05-05T14:48:00Z",
    "startTime": "2025-05-05T14:48:00Z",
    "finishTime": "2025-05-05T14:48:00Z",
    "sourceType": "001",
    "sourceBin": "01A01",
    "destType": "004",
    "destBin": "P099-01",
    "taskType": "成品出货",
    "msg": "success",
    "step": "finish",
    "agvId": ["AGV01","AGV03"]
  }],
  "message": "请求成功"
}
```

#### 失败示例

```json
{
  "status": "Fail",
  "data": [],
  "message": "连接失败"
}
```