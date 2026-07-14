namespace Mohist.Server.Infrastructure.Data.Events;

public static class EventReadKeys
{
    public const string TimeSortKeySql = """
        strftime('%Y-%m-%dT%H:%M:%S', "Time") ||
        substr(
            CASE
                WHEN instr(substr("Time", 20), '+') > 0 THEN substr("Time", 20, instr(substr("Time", 20), '+') - 1)
                WHEN instr(substr("Time", 20), '-') > 0 THEN substr("Time", 20, instr(substr("Time", 20), '-') - 1)
                ELSE ''
            END || '.0000000',
            1,
            8
        ) || 'Z'
        """;

    public const string DataStatusSql = """
        LOWER(COALESCE(json_extract("Data", '$.status'), json_extract("Data", '$.Status')))
        """;

    public const string PayloadStatusSql = """
        LOWER(COALESCE(json_extract("PayloadJson", '$.status'), json_extract("PayloadJson", '$.Status')))
        """;
}
