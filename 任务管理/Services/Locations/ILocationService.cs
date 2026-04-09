using System.Net.Sockets;
using System.Net;
using NModbus;
using WarehouseManagementSystem.Hubs.TcpClient.Hubs;
using System.Data;
using WarehouseManagementSystem.Models.IO;
using WarehouseManagementSystem.Db;
using Dapper;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using WarehouseManagementSystem.Hubs;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using WarehouseManagementSystem.Models;

public interface ILocationService
{
    Task<(IEnumerable<RCS_Locations> Items, int TotalItems)> GetLocations(string searchString = "", int page = 1, int pageSize = 10);

    Task<(IEnumerable<RCS_Locations> Items, int TotalCount)> GetSearchLocations(string searchString, int page, int pageSize);

    Task<RCS_Locations> GetLocationById(int id);

    // 添加按分组获取储位的方法
    Task<List<RCS_Locations>> GetLocationsByGroup(string group);

    Task<(bool Success, string Message)> CreateOrUpdateLocation(RCS_Locations location);
    Task<(bool Success, string Message)> HandleLocationOperation(int id, int type);
    Task<(int Available, int Used)> GetStorageCapacityStats();
    Task<(List<RCS_Locations> Items, int TotalItems, int Available, int Used)> GetLocationsWithStats(string searchString = "", int page = 1);

    // 按区域批量清空物料
    Task<(bool success, string message, int affectedCount)> BatchClearMaterials(string group);

    // 按区域批量锁定/解锁储位
    Task<(bool success, string message, int affectedCount)> BatchToggleLock(string group, bool lockState);

    // 按ID列表批量清空物料
    Task<(bool success, string message, int affectedCount)> BatchClearMaterialsByIds(List<int> locationIds);

    // 按ID列表批量锁定/解锁储位
    Task<(bool success, string message, int affectedCount)> BatchToggleLockByIds(List<int> locationIds, bool lockState);

    // 按区域批量设置数量（置满/置空）
    Task<(bool success, string message, int affectedCount)> BatchSetQuantityByGroup(string group, string quantity);

    // 按ID列表批量设置数量（置满/置空）
    Task<(bool success, string message, int affectedCount)> BatchSetQuantityByIds(List<int> locationIds, string quantity);


    Task<(bool success, string message, int affectedCount)> BatchUpdateMaterialCode(List<int> locationIds, string newMaterialCode);

    // 按区域批量清空物料编号
    Task<(bool success, string message, int affectedCount)> BatchClearMaterialCodeByGroup(string group);
}

public class LocationService : ILocationService
{
    private readonly IDatabaseService _db;
    private readonly ILogger<LocationService> _logger;

    public LocationService(IDatabaseService db, ILogger<LocationService> logger)
    {
        _db = db;
        _logger = logger;
    }


    public async Task<(bool success, string message, int affectedCount)> BatchUpdateMaterialCode(List<int> locationIds, string newMaterialCode)
    {
        using var connection = _db.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            if (locationIds == null || !locationIds.Any())
            {
                return (false, "请选择要修改的库位", 0);
            }

            if (string.IsNullOrWhiteSpace(newMaterialCode))
            {
                return (false, "新物料编号不能为空", 0);
            }

            // 更新指定ID的储位物料编号
            int affectedCount = await connection.ExecuteAsync(@"
                UPDATE RCS_Locations
                SET MaterialCode = @NewMaterialCode
                WHERE Id IN @LocationIds",
                new { LocationIds = locationIds, NewMaterialCode = newMaterialCode },
                transaction);

            transaction.Commit();
            return (true, $"成功修改 {affectedCount} 个储位的物料编号", affectedCount);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "批量修改物料编号失败");
            return (false, "批量修改失败：" + ex.Message, 0);
        }
    }


    public async Task<(bool success, string message, int affectedCount)> BatchClearMaterialsByIds(List<int> locationIds)
    {
        using var connection = _db.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            // 清空指定ID的储位物料信息
            int affectedCount = await connection.ExecuteAsync(@"
            UPDATE RCS_Locations
            SET MaterialCode = NULL,
                PalletID = '0',
                Weight = '0',
                Quanitity = '0',
                EntryDate = NULL
            WHERE Id IN @LocationIds",
                new { LocationIds = locationIds },
                transaction);

            transaction.Commit();
            return (true, $"成功清空 {affectedCount} 个储位的物料", affectedCount);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "批量清空储位物料失败");
            return (false, "清空物料失败，请稍后再试", 0);
        }
    }

    public async Task<(bool success, string message, int affectedCount)> BatchToggleLockByIds(List<int> locationIds, bool lockState)
    {
        using var connection = _db.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            // 执行批量锁定/解锁操作
            string operation = lockState ? "锁定" : "解锁";

            int affectedCount = await connection.ExecuteAsync(@"
            UPDATE RCS_Locations
            SET Lock = @LockState
            WHERE Id IN @LocationIds
            AND Lock <> @LockState",
                new
                {
                    LocationIds = locationIds,
                    LockState = lockState ? 1 : 0
                },
                transaction);

            transaction.Commit();
            return (true, $"成功{operation} {affectedCount} 个储位", affectedCount);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            string operation = lockState ? "锁定" : "解锁";
            _logger.LogError(ex, $"批量{operation}储位失败");
            return (false, $"批量{operation}失败，请稍后再试", 0);
        }
    }


    public async Task<(IEnumerable<RCS_Locations> Items, int TotalCount)> GetSearchLocations(string searchString, int page, int pageSize)
    {
        using var connection = _db.CreateConnection();

        try
        {
            // 构建基础查询
            var whereClause = string.IsNullOrEmpty(searchString)
                ? ""
                : "WHERE NodeRemark LIKE @Search OR MaterialCode LIKE @Search OR Name LIKE @Search";

            // 获取总记录数
            var countSql = $"SELECT COUNT(*) FROM RCS_Locations {whereClause}";
            var totalCount = await connection.ExecuteScalarAsync<int>(countSql,
                new { Search = $"%{searchString}%" });

            // 获取分页数据
            var sql = $@"
            SELECT * FROM 
            (
                SELECT *, ROW_NUMBER() OVER (ORDER BY [Group], NodeRemark) AS RowNum 
                FROM RCS_Locations 
                {whereClause}
            ) AS Paged 
            WHERE RowNum BETWEEN @Start AND @End";

            var start = (page - 1) * pageSize + 1;
            var end = start + pageSize - 1;

            var items = await connection.QueryAsync<RCS_Locations>(sql, new
            {
                Search = $"%{searchString}%",
                Start = start,
                End = end
            });

            return (items, totalCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取库位列表失败");
            throw;
        }
    }



    public async Task<(IEnumerable<RCS_Locations> Items, int TotalItems)> GetLocations(string searchString = "", int page = 1, int pageSize = 10)
    {
        try
        {
            using var conn = _db.CreateConnection();

            var query = "SELECT * FROM RCS_Locations";
            var countQuery = "SELECT COUNT(*) FROM RCS_Locations";
            var parameters = new DynamicParameters();

            if (!string.IsNullOrEmpty(searchString))
            {
                query += " WHERE NodeRemark LIKE @Search";
                countQuery += " WHERE NodeRemark LIKE @Search";
                parameters.Add("@Search", $"%{searchString}%");
            }

            query += " ORDER BY [Group], NodeRemark OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
            parameters.Add("@Offset", (page - 1) * pageSize);
            parameters.Add("@PageSize", pageSize);

            var items = await conn.QueryAsync<RCS_Locations>(query, parameters);
            var totalItems = await conn.ExecuteScalarAsync<int>(countQuery, parameters);

            return (items, totalItems);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取库位列表失败");
            throw;
        }
    }

    public async Task<(bool success, string message, int affectedCount)> BatchClearMaterials(string group)
    {
        using var connection = _db.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            // 找出指定分组中的所有有物料的储位
            //var locationsWithMaterials = await connection.QueryAsync<RCS_Locations>(@"
            //    SELECT * FROM RCS_Locations
            //    WHERE [Group] = @Group 
            //    AND MaterialCode IS NOT NULL 
            //    AND MaterialCode <> ''",
            //    new { Group = group },
            //    transaction);

            //if (!locationsWithMaterials.Any())
            //{
            //    transaction.Commit();
            //    return (true, $"区域 {group} 没有需要清空的物料", 0);
            //}

            // 清空这些储位的物料信息
            int affectedCount = await connection.ExecuteAsync(@"
                UPDATE RCS_Locations
                SET MaterialCode = NULL,
                    PalletID = '0',
                    Weight = '0',
                    Quanitity = '',
                    EntryDate = NULL
                WHERE [Group] = @Group 
                AND MaterialCode IS NOT NULL 
                AND MaterialCode <> ''",
                new { Group = group },
                transaction);

            transaction.Commit();
            return (true, $"成功清空区域 {group} 中的 {affectedCount} 个储位物料", affectedCount);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, $"批量清空区域 {group} 物料失败");
            return (false, "清空物料失败，请稍后再试", 0);
        }
    }

    public async Task<(bool success, string message, int affectedCount)> BatchToggleLock(string group, bool lockState)
    {
        using var connection = _db.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            // 执行批量锁定/解锁操作
            string operation = lockState ? "锁定" : "解锁";

            // 只操作与目标状态不同的储位
            int affectedCount = await connection.ExecuteAsync(@"
                    UPDATE RCS_Locations
                    SET Lock = @LockState
                    WHERE [Group] = @Group 
                    AND Lock <> @LockState",
                new { Group = group, LockState = lockState ? 1 : 0 },
                transaction);

            transaction.Commit();
            return (true, $"成功{operation}区域 {group} 中的 {affectedCount} 个储位", affectedCount);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            string operation = lockState ? "锁定" : "解锁";
            _logger.LogError(ex, $"批量{operation}区域 {group} 储位失败");
            return (false, $"批量{operation}失败，请稍后再试", 0);
        }
    }



    public async Task<RCS_Locations> GetLocationById(int id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<RCS_Locations>(
            "SELECT * FROM RCS_Locations WHERE Id = @Id",
            new { Id = id });
    }

    public async Task<(bool Success, string Message)> CreateOrUpdateLocation(RCS_Locations location)
    {
        try
        {
            using var conn = _db.CreateConnection();

            var existing = await conn.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                new { location.NodeRemark });

            if (existing != null)
            {
                var sql = @"UPDATE RCS_Locations 
                SET Name = @Name, NodeRemark = @NodeRemark, MaterialCode = @MaterialCode, 
                    PalletID = @PalletID, Weight = @Weight, Quanitity = @Quanitity, 
                    EntryDate = @EntryDate, [Group] = @Group, LiftingHeight = @LiftingHeight, 
                    Lock = @Lock, WattingNode = @WattingNode,UnloadHeight=@UnloadHeight 
                WHERE NodeRemark = @NodeRemark";

                await conn.ExecuteAsync(sql, location);
                return (true, "修改成功");
            }
            else
            {
                var sql = @"INSERT INTO RCS_Locations 
                (Name, NodeRemark, MaterialCode, PalletID, Weight, Quanitity, 
                    EntryDate, [Group], LiftingHeight, Lock, WattingNode,UnloadHeight) 
                VALUES 
                (@Name, @NodeRemark, @MaterialCode, @PalletID, @Weight, @Quanitity, 
                    @EntryDate, @Group, @LiftingHeight, @Lock, @WattingNode,@UnloadHeight)";

                await conn.ExecuteAsync(sql, location);
                return (true, "新存储位置已成功创建！");
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存库位信息失败");
            throw;
        }
    }

    public async Task<(bool Success, string Message)> HandleLocationOperation(int id, int type)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        using var transaction = conn.BeginTransaction();

        try
        {
            var location = await conn.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE Id = @Id",
                new { Id = id },
                transaction);

            if (location == null)
            {
                return (false, "操作失败，找不到该储位。");
            }

            switch (type)
            {
                case 1: // 删除
                    await conn.ExecuteAsync("DELETE FROM RCS_Locations WHERE Id = @Id", new { Id = id }, transaction);
                    transaction.Commit();
                    return (true, "储位删除成功！");

                case 2: // 解锁/锁定
                    await HandleLockOperation(conn, transaction, location);
                    transaction.Commit();
                    return (true, "储位锁定状态修改成功！");

                case 3: // 清空物料
                    await HandleClearMaterialOperation(conn, transaction, location);
                    transaction.Commit();
                    return (true, "物料清空成功！");

                case 4: // 重置异常物料
                    if (location.MaterialCode != null && location.MaterialCode.StartsWith("Err_"))
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE RCS_Locations 
                            SET MaterialCode = @MaterialCode 
                            WHERE Id = @Id",
                            new { Id = id, MaterialCode = location.MaterialCode.Replace("Err_", "") },
                            transaction);
                        transaction.Commit();
                        return (true, "异常物料重置成功！");
                    }
                    return (false, "该储位不包含异常物料！");

                default:
                    return (false, "无效的操作类型！");
            }
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "操作失败");
            return (false, "操作失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 处理锁定/解锁操作，包括同步操作对应的中转点
    /// </summary>
    private async Task HandleLockOperation(IDbConnection conn, IDbTransaction transaction, RCS_Locations location)
    {
        bool newLockState = !location.Lock;

        // 更新当前储位的锁定状态
        await conn.ExecuteAsync(
            "UPDATE RCS_Locations SET Lock = @Lock WHERE Id = @Id",
            new { Id = location.Id, Lock = newLockState },
            transaction);

        // 检查是否需要同步操作对应的中转点
        string? correspondingLocationName = GetCorrespondingTransferPoint(location.Name);
        if (!string.IsNullOrEmpty(correspondingLocationName))
        {
            // 查找对应的中转点
            var correspondingLocation = await conn.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE Name = @Name",
                new { Name = correspondingLocationName },
                transaction);

            if (correspondingLocation != null)
            {
                // 同步更新对应中转点的锁定状态
                await conn.ExecuteAsync(
                    "UPDATE RCS_Locations SET Lock = @Lock WHERE Name = @Name",
                    new { Name = correspondingLocationName, Lock = newLockState },
                    transaction);

                _logger.LogInformation("同步操作中转点: {LocationName} -> {CorrespondingName}, 锁定状态: {LockState}",
                    location.Name, correspondingLocationName, newLockState);
            }
        }
    }

    /// <summary>
    /// 处理清空物料操作，包括同步操作对应的中转点
    /// </summary>
    private async Task HandleClearMaterialOperation(IDbConnection conn, IDbTransaction transaction, RCS_Locations location)
    {
        // 清空当前储位的物料信息
        await conn.ExecuteAsync(@"
            UPDATE RCS_Locations 
            SET MaterialCode = NULL, PalletID = NULL, Weight = '0', Quanitity = '0' 
            WHERE Id = @Id",
            new { Id = location.Id },
            transaction);

        // 检查是否需要同步操作对应的中转点
        string? correspondingLocationName = GetCorrespondingTransferPoint(location.Name);
        if (!string.IsNullOrEmpty(correspondingLocationName))
        {
            // 查找对应的中转点
            var correspondingLocation = await conn.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE Name = @Name",
                new { Name = correspondingLocationName },
                transaction);

            if (correspondingLocation != null)
            {
                // 同步清空对应中转点的物料信息
                await conn.ExecuteAsync(@"
                    UPDATE RCS_Locations 
                    SET MaterialCode = NULL, PalletID = NULL, Weight = '0', Quanitity = '0' 
                    WHERE Name = @Name",
                    new { Name = correspondingLocationName },
                    transaction);

                _logger.LogInformation("同步清空中转点物料: {LocationName} -> {CorrespondingName}",
                    location.Name, correspondingLocationName);
            }
        }
    }

    /// <summary>
    /// 获取对应的中转点名称
    /// 小地牛中转点(71-79) <-> 三向车中转点(596-604)
    /// </summary>
    /// <param name="locationName">当前储位的Name</param>
    /// <returns>对应的中转点名称，如果没有对应关系则返回null</returns>
    private string? GetCorrespondingTransferPoint(string locationName)
    {
        // 小地牛中转点(71-79) -> 三向车中转点(596-604)
        if (int.TryParse(locationName, out int smallAgvNum) && smallAgvNum >= 71 && smallAgvNum <= 79)
        {
            int correspondingThreeWay = smallAgvNum + 525; // 71 + 525 = 596, 72 + 525 = 597, ...
            return correspondingThreeWay.ToString();
        }

        // 三向车中转点(596-604) -> 小地牛中转点(71-79)
        if (int.TryParse(locationName, out int threeWayNum) && threeWayNum >= 596 && threeWayNum <= 604)
        {
            int correspondingSmallAgv = threeWayNum - 525; // 596 - 525 = 71, 597 - 525 = 72, ...
            return correspondingSmallAgv.ToString();
        }

        // 没有对应关系
        return null;
    }

    public async Task<(int Available, int Used)> GetStorageCapacityStats()
    {
        try
        {
            using var conn = _db.CreateConnection();
            var locations = await conn.QueryAsync<RCS_Locations>("SELECT MaterialCode FROM RCS_Locations");

            var used = locations.Count(loc =>
                !string.IsNullOrEmpty(loc.MaterialCode) && loc.MaterialCode != "empty");
            var total = locations.Count();

            return (total, used);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取存储容量统计失败");
            throw;
        }
    }

    public async Task<(List<RCS_Locations> Items, int TotalItems, int Available, int Used)> GetLocationsWithStats(string searchString = "", int page = 1)
    {
        try
        {
            using var conn = _db.CreateConnection();
            const int pageSize = 15; // 修改为每页显示15条数据

            // 构建查询
            var query = "SELECT * FROM RCS_Locations";
            var countQuery = "SELECT COUNT(*) FROM RCS_Locations";
            var parameters = new DynamicParameters();

            if (!string.IsNullOrEmpty(searchString))
            {
                query += " WHERE NodeRemark LIKE @Search";
                countQuery += " WHERE NodeRemark LIKE @Search";
                parameters.Add("@Search", $"%{searchString}%");
            }

            // 添加分页
            query += " ORDER BY [Group], NodeRemark OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
            parameters.Add("@Offset", (page - 1) * pageSize);
            parameters.Add("@PageSize", pageSize);

            // 执行查询
            var items = await conn.QueryAsync<RCS_Locations>(query, parameters);
            var totalItems = await conn.ExecuteScalarAsync<int>(countQuery, parameters);

            // 获取统计信息
            var allLocations = await conn.QueryAsync<RCS_Locations>("SELECT MaterialCode FROM RCS_Locations");
            var used = allLocations.Count(loc =>
                !string.IsNullOrEmpty(loc.MaterialCode) && loc.MaterialCode != "empty");
            var total = allLocations.Count();

            return (items.ToList(), totalItems, total, used);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取库位列表和统计信息失败");
            throw;
        }
    }

    public async Task<(bool success, string message, int affectedCount)> BatchSetQuantityByGroup(string group, string quantity)
    {
        using var connection = _db.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            int affectedCount = await connection.ExecuteAsync(@"
                UPDATE RCS_Locations
                SET Quanitity = @Quantity
                WHERE [Group] = @Group",
                new { Group = group, Quantity = quantity }, transaction);
            transaction.Commit();
            return (true, $"成功设置区域 {group} 中的 {affectedCount} 个储位数量为{quantity}", affectedCount);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, $"批量设置区域 {group} 数量失败");
            return (false, "批量设置数量失败，请稍后再试", 0);
        }
    }

    public async Task<(bool success, string message, int affectedCount)> BatchSetQuantityByIds(List<int> locationIds, string quantity)
    {
        using var connection = _db.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            int affectedCount = await connection.ExecuteAsync(@"
                UPDATE RCS_Locations
                SET Quanitity = @Quantity
                WHERE Id IN @LocationIds",
                new { LocationIds = locationIds, Quantity = quantity }, transaction);
            transaction.Commit();
            return (true, $"成功设置 {affectedCount} 个储位数量为{quantity}", affectedCount);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, $"批量设置储位数量失败");
            return (false, "批量设置数量失败，请稍后再试", 0);
        }
    }

    public async Task<List<RCS_Locations>> GetLocationsByGroup(string group)
    {
        try
        {
            using var conn = _db.CreateConnection();
            var locations = await conn.QueryAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE [Group] = @Group",
                new { Group = group });
            return locations.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"获取分组 {group} 的储位失败");
            throw;
        }
    }

    public async Task<(bool success, string message, int affectedCount)> BatchClearMaterialCodeByGroup(string group)
    {
        using var connection = _db.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            // 清空指定分组中的物料编号
            int affectedCount = await connection.ExecuteAsync(@"
                UPDATE RCS_Locations
                SET MaterialCode = NULL,Quanitity = 0
                WHERE [Group] = @Group",
                new { Group = group },
                transaction);

            transaction.Commit();
            return (true, $"成功清空区域 {group} 中的 {affectedCount} 个储位物料编号", affectedCount);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, $"批量清空区域 {group} 物料编号失败");
            return (false, "清空物料编号失败，请稍后再试", 0);
        }
    }
}