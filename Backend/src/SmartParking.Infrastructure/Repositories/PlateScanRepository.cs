
using Dapper;
using SmartParking.Application.Interfaces;
using SmartParking.Domain.Entities;
using SmartParking.Infrastructure.Data;

namespace SmartParking.Infrastructure.Repositories;

public class PlateScanRepository : IPlateScanRepository
{
    private readonly DbConnectionFactory _factory;

    public PlateScanRepository(DbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<PlateScan>> GetAllAsync(string? search = null)
    {
        const string baseSql = """
            SELECT
                scan_id AS ScanId,
                plate AS Plate,
                confidence AS Confidence,
                image_path AS ImagePath,
                crop_image_path AS CropImagePath,
                plate_color_code AS PlateColorCode,
                plate_color_label AS PlateColorLabel,
                plate_color_confidence AS PlateColorConfidence,
                scan_time AS ScanTime,
                note AS Note,
                color_code AS ColorCode,
                created_at AS CreatedAt
            FROM plate_scans
            """;

        var sql = baseSql;

        if (!string.IsNullOrWhiteSpace(search))
        {
            sql += " WHERE plate ILIKE @Search ";
        }

        sql += " ORDER BY scan_id DESC;";

        using var db = _factory.CreateConnection();

        var rows = await db.QueryAsync<PlateScan>(
            sql,
            new { Search = $"%{search}%" });

        return rows.ToList();
    }

    public async Task<PlateScan?> GetByIdAsync(long id)
    {
        const string sql = """
            SELECT
                scan_id AS ScanId,
                plate AS Plate,
                confidence AS Confidence,
                image_path AS ImagePath,
                crop_image_path AS CropImagePath,
                plate_color_code AS PlateColorCode,
                plate_color_label AS PlateColorLabel,
                plate_color_confidence AS PlateColorConfidence,
                scan_time AS ScanTime,
                note AS Note,
                color_code AS ColorCode,
                created_at AS CreatedAt
            FROM plate_scans
            WHERE scan_id = @Id;
            """;

        using var db = _factory.CreateConnection();

        return await db.QuerySingleOrDefaultAsync<PlateScan>(
            sql,
            new { Id = id });
    }

    public async Task<long> CreateAsync(PlateScan scan)
    {
        const string sql = """
            INSERT INTO plate_scans
            (
                plate,
                confidence,
                image_path,
                crop_image_path,
                plate_color_code,
                plate_color_label,
                plate_color_confidence,
                scan_time,
                note,
                color_code,
                created_at
            )
            VALUES
            (
                @Plate,
                @Confidence,
                @ImagePath,
                @CropImagePath,
                @PlateColorCode,
                @PlateColorLabel,
                @PlateColorConfidence,
                @ScanTime,
                @Note,
                @ColorCode,
                @CreatedAt
            )
            RETURNING scan_id;
            """;

        using var db = _factory.CreateConnection();

        return await db.ExecuteScalarAsync<long>(sql, scan);
    }

    public async Task<bool> DeleteAsync(long id)
    {
        const string sql = """
            DELETE FROM plate_scans
            WHERE scan_id = @Id;
            """;

        using var db = _factory.CreateConnection();

        return await db.ExecuteAsync(sql, new { Id = id }) > 0;
    }
}
