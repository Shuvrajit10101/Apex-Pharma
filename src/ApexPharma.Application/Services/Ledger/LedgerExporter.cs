using System.Globalization;
using System.Text;
using ApexPharma.Application.Services.Reporting;
using QuestPDF.Fluent;

namespace ApexPharma.Application.Services.Ledger;

/// <summary>
/// Concrete <see cref="ILedgerExporter"/>. CSV is built with RFC-4180 quoting (fields with a
/// comma, quote, or newline are wrapped and inner quotes doubled) so it opens cleanly in
/// Excel/LibreOffice; the caller persists it UTF-8 with a BOM via <c>IReportFileService</c>. PDF
/// is rendered through the <see cref="PartyStatementDocument"/> QuestPDF document (A4). Both are
/// pure formatting over the <see cref="PartyStatement"/> DTO — no database access — so the output
/// is deterministic and unit-testable (mirrors <see cref="ReportExporter"/>).
/// </summary>
public sealed class LedgerExporter : ILedgerExporter
{
    private const string DateFormat = "yyyy-MM-dd";

    /// <inheritdoc />
    public string PartyStatementCsv(PartyStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var sb = new StringBuilder();
        AppendRow(sb, "Date", "Type", "Reference", "Debit", "Credit", "Balance");

        // One row per statement row (the synthetic opening row is already first in Rows).
        foreach (PartyStatementRow r in statement.Rows)
        {
            AppendRow(sb,
                r.Date.ToString(DateFormat, CultureInfo.InvariantCulture),
                r.DocType,
                r.RefNo,
                Money(r.Debit),
                Money(r.Credit),
                Money(r.RunningBalance));
        }

        // Trailing closing-balance row so the statement foots on its own.
        AppendRow(sb, statement.ToDate.ToString(DateFormat, CultureInfo.InvariantCulture),
            "Closing balance", string.Empty, string.Empty, string.Empty, Money(statement.ClosingBalance));
        return sb.ToString();
    }

    /// <inheritdoc />
    public byte[] PartyStatementPdf(ReportHeader header, PartyStatement statement)
        => new PartyStatementDocument(header, statement).GeneratePdf();

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Appends one CSV record, quoting each field per RFC 4180, terminated with CRLF.</summary>
    private static void AppendRow(StringBuilder sb, params string[] fields)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Escape(fields[i]));
        }

        sb.Append("\r\n");
    }

    private static string Escape(string field)
    {
        field ??= string.Empty;
        bool mustQuote = field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r');
        if (!mustQuote)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
