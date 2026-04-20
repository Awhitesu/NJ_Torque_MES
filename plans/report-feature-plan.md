# 数据报表与历史记录查询功能 - 开发计划

## 一、技术决策确认

| 决策项 | 选择 |
|-------|------|
| 数据库 | SQLite + EF Core |
| 图表方案 | 原生 Canvas / CSS |
| 导出格式 | CSV (前端生成) |
| 数据保存时机 | 批次完成时一次性写入 |

## 二、数据库模型

### TighteningRecord (单次拧紧记录)
- Id (PK, long)
- BatchId (string)
- ProductCode (string)
- OrderCode (string)
- RouteNo (string)
- WorkstepNo (string)
- WorkstepName (string)
- ScrewIndex (int)
- PSetNo (string)
- TorqueMin/Max (double)
- ActualTorque (double)
- ActualAngle (double)
- Result (int: 0=PASS, 1=FAIL)
- RetryCount (int)
- Operator (string)
- Timestamp (DateTime)

### ProductionBatch (生产批次)
- Id (PK, long)
- BatchId (string, unique)
- ProductCode (string)
- OrderCode (string)
- RouteNo (string)
- TotalScrews (int)
- PassCount (int)
- FailCount (int)
- MaterialVerified (bool)
- StartTime (DateTime)
- EndTime (DateTime?)
- Operator (string)
- Status (int)

### DailyStatistics (日统计)
- Id (PK, long)
- Date (DateTime)
- TotalBatches (int)
- TotalScrews (int)
- PassCount (int)
- FailCount (int)
- PassRate (double)
- AvgTorque (double?)
- AvgAngle (double?)

## 三、API 设计

| 端点 | 方法 | 功能 |
|-----|------|------|
| /api/reports/records | GET | 分页查询拧紧记录 |
| /api/reports/batches | GET | 查询生产批次 |
| /api/reports/batch/{id} | GET | 批次详情 |
| /api/reports/daily | GET | 日统计 |
| /api/reports/summary | GET | 汇总看板 |
| /api/reports/save | POST | 保存批次数据 |

## 四、前端组件

- ReportView.vue - 主报表容器
- ReportHistoryTable.vue - 历史记录表格
- ReportStatistics.vue - 统计图表 (Canvas)
- reportApi.ts - API 封装

## 五、集成点

1. App.vue handleAllTasksComplete -> 调用 /api/reports/save
2. App.vue 标签栏添加 "数据报表" 入口
3. TorqueControllerService 可选：实时保存每次拧紧记录
