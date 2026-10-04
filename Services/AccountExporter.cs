using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BookmarkVault.Models;

namespace BookmarkVault.Services;

/// <summary>导出成表格时的文件格式</summary>
public enum AccountExportFormat
{
    /// <summary>逗号分隔的纯文本表格，Excel 可以直接打开</summary>
    Csv = 0,
    /// <summary>真正的 Excel 工作簿（手写 OOXML，不需要额外依赖）</summary>
    Xlsx = 1
}

/// <summary>
/// 把账号库导出成表格。两种格式都是本机直接写文件，不联网、不加新依赖。
/// </summary>
public static class AccountExporter
{
    public const string CsvExtension = ".csv";
    public const string XlsxExtension = ".xlsx";

    /// <summary>把 accounts 写进 path。includePassword 为真时才把解密后的明文密码写进去</summary>
    public static void Export(IEnumerable<AccountEntry> accounts, string path, AccountExportFormat format, bool includePassword)
    {
        var rows = accounts.ToList();
        if (format == AccountExportFormat.Csv) WriteCsv(rows, path, includePassword);
        else WriteXlsx(rows, path, includePassword);
    }

    // ---------- 公共部分 ----------

    private static string[] Headers(bool includePassword) => includePassword
        ? new[] { "域名", "站点名称", "账号 / 邮箱", "密码", "备注", "自动提示", "更新时间" }
        : new[] { "域名", "站点名称", "账号 / 邮箱", "备注", "自动提示", "更新时间" };

    private static string[] Cells(AccountEntry account, bool includePassword)
    {
        var cells = new List<string> { account.Domain, account.SiteName, account.Username };
        if (includePassword) cells.Add(AccountVault.Decrypt(account.PasswordCipher) ?? string.Empty);
        cells.Add(account.Note ?? string.Empty);
        cells.Add(account.AutoPrompt ? "是" : "否");
        cells.Add(account.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        return cells.ToArray();
    }

    // ---------- CSV ----------

    private static void WriteCsv(List<AccountEntry> rows, string path, bool includePassword)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Headers(includePassword).Select(EscapeCsv))).Append("\r\n");
        foreach (var row in rows)
            sb.Append(string.Join(",", Cells(row, includePassword).Select(EscapeCsv))).Append("\r\n");

        // 必须带 BOM，否则 Excel 打开中文会乱码
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string EscapeCsv(string value)
    {
        value ??= string.Empty;

        // Excel 会把 = + - @ 开头的单元格当公式执行（CSV 注入），前面加个单引号让它当纯文本。
        // 这一条只影响 CSV；xlsx 里单元格显式声明为字符串，不受影响，也不会改字符。
        if (value.Length > 0 && (value[0] == '=' || value[0] == '+' || value[0] == '-' ||
                                 value[0] == '@' || value[0] == '\t' || value[0] == '\r'))
            value = "'" + value;

        if (value.IndexOfAny(new[] { '"', ',', '\n', '\r', '\t' }) >= 0)
            value = "\"" + value.Replace("\"", "\"\"") + "\"";

        return value;
    }

    // ---------- xlsx ----------

    private static void WriteXlsx(List<AccountEntry> rows, string path, bool includePassword)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        AddPart(zip, "[Content_Types].xml", ContentTypes);
        AddPart(zip, "_rels/.rels", RootRels);
        AddPart(zip, "xl/workbook.xml", Workbook);
        AddPart(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
        AddPart(zip, "xl/styles.xml", Styles);
        AddPart(zip, "xl/worksheets/sheet1.xml", BuildSheet(rows, includePassword));
    }

    private static void AddPart(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string BuildSheet(List<AccountEntry> rows, bool includePassword)
    {
        var headers = Headers(includePassword);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        sb.Append("<cols>");
        for (var i = 0; i < headers.Length; i++)
            sb.Append("<col min=\"").Append(i + 1).Append("\" max=\"").Append(i + 1)
              .Append("\" width=\"").Append(ColumnWidth(i)).Append("\" customWidth=\"1\"/>");
        sb.Append("</cols>");

        sb.Append("<sheetData>");
        sb.Append("<row r=\"1\">");
        for (var c = 0; c < headers.Length; c++)
            sb.Append(CellText(ColumnName(c + 1) + "1", headers[c], true));
        sb.Append("</row>");

        for (var r = 0; r < rows.Count; r++)
        {
            var values = Cells(rows[r], includePassword);
            var line = r + 2;
            sb.Append("<row r=\"").Append(line).Append("\">");
            for (var c = 0; c < values.Length; c++)
                sb.Append(CellText(ColumnName(c + 1) + line, values[c], false));
            sb.Append("</row>");
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    /// <summary>单元格统一写成 inlineStr（字符串），Excel 不会当公式处理，密码里的前导 0 也不会丢</summary>
    private static string CellText(string reference, string value, bool bold)
    {
        var sb = new StringBuilder();
        sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"");
        if (bold) sb.Append(" s=\"1\"");
        sb.Append("><is><t xml:space=\"preserve\">").Append(EscapeXml(value)).Append("</t></is></c>");
        return sb.ToString();
    }

    private static string EscapeXml(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                case '\t': sb.Append("&#9;"); break;
                case '\n': sb.Append("&#10;"); break;
                case '\r': sb.Append("&#13;"); break;
                default:
                    // XML 1.0 不接受的控制字符，丢掉，否则 Excel 会判定文件损坏
                    if (ch < 0x20) break;
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static double ColumnWidth(int index) => index switch
    {
        0 => 26,  // 域名
        1 => 20,  // 站点名称
        2 => 26,  // 账号 / 邮箱
        3 => 24,  // 密码或备注
        4 => 24,
        _ => 20
    };

    /// <summary>0 → A，25 → Z，26 → AA</summary>
    private static string ColumnName(int index)
    {
        var name = string.Empty;
        while (index > 0)
        {
            var remainder = (index - 1) % 26;
            name = (char)('A' + remainder) + name;
            index = (index - 1) / 26;
        }
        return name;
    }

    // ---------- xlsx 骨架（最小可用的 OOXML 包） ----------

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "</Types>";

    private const string RootRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string Workbook =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"账号库\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        "</workbook>";

    private const string WorkbookRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    /// <summary>两个字体 + 两个填充（none / gray125），Excel 要求 fills 至少两项</summary>
    private const string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"2\">" +
        "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
        "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
        "</fonts>" +
        "<fills count=\"2\">" +
        "<fill><patternFill patternType=\"none\"/></fill>" +
        "<fill><patternFill patternType=\"gray125\"/></fill>" +
        "</fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"2\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";
}