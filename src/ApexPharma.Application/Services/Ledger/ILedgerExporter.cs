using ApexPharma.Application.Services.Reporting;

namespace ApexPharma.Application.Services.Ledger;

/// <summary>
/// Turns a derived <see cref="PartyStatement"/> into portable artifacts (plan.md §11): CSV text
/// the accountant can open, and a printable A4 PDF. Kept separate from <see cref="IReportExporter"/>
/// so the report exporter stays focused. Pure formatting — no data access — so the CSV shape is
/// unit-testable without a database; PDF bytes are produced via QuestPDF.
/// </summary>
public interface ILedgerExporter
{
    /// <summary>
    /// CSV for a party statement (RFC-4180 quoting): a header row, then one row per statement row
    /// including the opening-balance and every in-window row, with a trailing closing-balance row.
    /// </summary>
    string PartyStatementCsv(PartyStatement statement);

    /// <summary>Renders a party statement to PDF bytes (A4 portrait).</summary>
    byte[] PartyStatementPdf(ReportHeader header, PartyStatement statement);
}
