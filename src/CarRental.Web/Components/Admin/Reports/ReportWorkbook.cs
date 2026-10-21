using System.Globalization;
using CarRental.Application.Reporting.Indicators;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using CarRental.Web.Resources.Admin.Reports;
using ClosedXML.Excel;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Components.Admin.Reports;

/// <summary>
/// The Excel workbook of an export of the reports (ClosedXML, ADR-0046): the sheet of the table (or of every table) and
/// the summary sheet (agency, period, comparison, coverage of the snapshots, headline indicators and notes on the
/// sources), in the user's language. Values keep their type: amounts as numbers to the millime, ratios as percentages,
/// days as dates, waiting times as durations; a value that cannot be computed is an empty cell. Every table sheet has a
/// bold frozen header with a filter, and its total row below the filtered range.
/// </summary>
internal sealed class ReportWorkbook
{
    private const string AmountFormat = "#,##0.000";
    private const string CountFormat = "#,##0";
    private const string RatioFormat = "0.0%";
    private const string DurationFormat = "[h]:mm";
    private const string InstantFormat = "yyyy-mm-dd hh:mm";
    private const int ShortDateFormatId = 14;

    private static readonly XLColor HeaderFill = XLColor.FromHtml("#E8EEF7");
    private static readonly XLColor SubtotalFill = XLColor.FromHtml("#F4F6F9");

    private readonly ReportDashboard _report;
    private readonly IStringLocalizer _l;

    private ReportWorkbook(ReportDashboard report, IStringLocalizer localizer)
    {
        _report = report;
        _l = localizer;
    }

    /// <summary>The sheets of an export, the table first.</summary>
    public static IReadOnlyList<ReportTable> SheetsOf(ReportTable table) => table == ReportTable.All
        ?
        [
            ReportTable.All,
            ReportTable.Daily,
            ReportTable.Vehicles,
            ReportTable.Channels,
            ReportTable.Pace,
            ReportTable.Funnel,
            ReportTable.Requests,
            ReportTable.Cancellations,
            ReportTable.Cash,
            ReportTable.Unpaid,
        ]
        : [table, ReportTable.All];

    /// <summary>The workbook (.xlsx) of <paramref name="table"/>; <see cref="ReportTable.All"/> is the summary sheet itself.</summary>
    public static byte[] Build(ReportDashboard report, ReportTable table, IStringLocalizer<ReportsResources> localizer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(localizer);
        var builder = new ReportWorkbook(report, localizer);
        using var workbook = new XLWorkbook();
        foreach (var sheet in SheetsOf(table))
        {
            builder.Add(workbook.Worksheets.Add(localizer[$"Sheet.{sheet}"]), sheet);
        }

        workbook.Worksheets.First().SetTabActive();
        workbook.Properties.Title = localizer["Export.Title", report.AgencyName, ReportFormat.Date(report.Period.From), ReportFormat.Date(report.Period.To)];
        workbook.Properties.Author = "CarRental";
        workbook.Properties.Created = TunisTime.ToTunis(report.GeneratedAtUtc).DateTime;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private void Add(IXLWorksheet sheet, ReportTable table)
    {
        switch (table)
        {
            case ReportTable.All:
                Summary(sheet);
                break;
            case ReportTable.Daily:
                Daily(sheet);
                break;
            case ReportTable.Vehicles:
                Vehicles(sheet);
                break;
            case ReportTable.Channels:
                Channels(sheet);
                break;
            case ReportTable.Pace:
                Pace(sheet);
                break;
            case ReportTable.Funnel:
                Funnel(sheet);
                break;
            case ReportTable.Requests:
                Requests(sheet);
                break;
            case ReportTable.Cancellations:
                Cancellations(sheet);
                break;
            case ReportTable.Cash:
                Cash(sheet);
                break;
            case ReportTable.Unpaid:
                Unpaid(sheet);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(table), table, null);
        }
    }

    private void Summary(IXLWorksheet sheet)
    {
        var period = _report.Period;
        var writer = new SheetWriter(sheet);
        writer.Title(_l["Export.Title", _report.AgencyName, ReportFormat.Date(period.From), ReportFormat.Date(period.To)]);
        writer.Pair(_l["Summary.Agency"], _report.AgencyName);
        writer.Pair(_l["Summary.Period"], Range(period.From, period.To));
        writer.Pair(_l["Summary.Covered"], period.CoveredTo is { } coveredTo ? Range(period.From, coveredTo) : _l["Summary.NoElapsedDay"]);
        writer.Pair(_l["Summary.Compared"], period.PreviousFrom is { } previousFrom && period.PreviousTo is { } previousTo ? Range(previousFrom, previousTo) : ReportFormat.None);
        writer.Pair(_l["Summary.Snapshots"], _l["Summary.Snapshots.Value", _report.Coverage.ComputedDays, _report.Coverage.ExpectedDays]);
        if (_report.PreviousCoverage is { } previousCoverage)
        {
            writer.Pair(_l["Summary.PreviousSnapshots"], _l["Summary.Snapshots.Value", previousCoverage.ComputedDays, previousCoverage.ExpectedDays]);
        }

        writer.Pair(_l["Summary.Generated"], TunisTime.ToTunis(_report.GeneratedAtUtc).DateTime, Kind.Instant);
        writer.Skip();

        // The fleet compares only with the snapshots of the previous period: none, no comparison.
        var fleet = _report.Fleet;
        var previous = _report.PreviousCoverage is { ComputedDays: > 0 } ? _report.PreviousFleet : null;
        writer.Header(
            (_l["Summary.Indicator"], Kind.Text),
            (_l["Summary.Current"], Kind.Text),
            (_l["Summary.Previous"], Kind.Text),
            (_l["Summary.Change"], Kind.Ratio));
        Indicator(writer, "Kpi.RevPav", fleet.RevenuePerAvailableDay, previous?.RevenuePerAvailableDay);
        Indicator(writer, "Kpi.Utilization", fleet.Utilization, previous?.Utilization);
        Indicator(writer, "Kpi.AverageRentedDayPrice", fleet.AverageRentedDayPrice, previous?.AverageRentedDayPrice);
        Indicator(writer, "Kpi.Revenue", fleet.Revenue, previous?.Revenue);
        Indicator(writer, "Kpi.Commission", fleet.Commission, previous?.Commission);
        Indicator(writer, "Kpi.DirectCosts", fleet.DirectCosts, previous?.DirectCosts);
        Indicator(writer, "Kpi.Depreciation", fleet.Depreciation, previous?.Depreciation);
        Indicator(writer, "Kpi.Margin", fleet.Margin, previous?.Margin);
        Indicator(writer, "Kpi.MarginPerAvailableDay", fleet.MarginPerAvailableDay, previous?.MarginPerAvailableDay);
        Indicator(writer, "Kpi.AvailableDays", fleet.AvailableDays, previous?.AvailableDays);
        Indicator(writer, "Kpi.RentedDays", fleet.RentedDays, previous?.RentedDays);
        Indicator(writer, "Kpi.CollectedNet", _report.Cash.CollectedNet, (Money?)_report.Cash.PreviousCollectedNet);
        if (_report.Sources.InvoicingConnected)
        {
            Indicator(writer, "Kpi.Invoiced", _report.Cash.Invoiced.ExclVat, null);
        }

        Indicator(writer, "Kpi.Unpaid", _report.Cash.UnpaidTotal, null);
        Indicator(writer, "Kpi.OpenDeposits", _report.Cash.OpenDeposits, null);
        Indicator(writer, "Kpi.Conversion", _report.Funnel.Total.SearchToConfirmed, _report.PreviousFunnel?.Total.SearchToConfirmed);
        Indicator(writer, "Kpi.RefusalRate", _report.Requests.Total.RefusalRate, _report.PreviousRequests?.Total.RefusalRate);
        Indicator(writer, "Kpi.CancellationRate", _report.Cancellations.Total.CancellationRate, _report.PreviousCancellations?.Total.CancellationRate);
        Indicator(writer, "Kpi.NoShowRate", _report.Cancellations.Total.NoShowRate, _report.PreviousCancellations?.Total.NoShowRate);
        writer.Skip();

        writer.Title(_l["Summary.Notes"]);
        foreach (var note in Notes())
        {
            writer.Note(note);
        }

        writer.Finish(filter: false);
    }

    private void Indicator(SheetWriter writer, string key, Money? current, Money? previous) =>
        writer.Row(_l["Summary.InTnd", _l[key]].Value, (object?)current ?? ReportFormat.None, (object?)previous ?? ReportFormat.None, KpiMath.Change(current, previous));

    private void Indicator(SheetWriter writer, string key, decimal? current, decimal? previous) =>
        writer.Row(_l[key].Value, current is { } now ? new Ratio(now) : ReportFormat.None, previous is { } before ? new Ratio(before) : ReportFormat.None, (object?)null);

    private void Indicator(SheetWriter writer, string key, int current, int? previous) =>
        writer.Row(_l[key].Value, current, (object?)previous ?? ReportFormat.None, previous is { } before ? KpiMath.Change(current, before) : null);

    /// <summary>What a reader must know about the figures: the sources not connected yet and where costs come from.</summary>
    public IEnumerable<string> Notes()
    {
        var sources = _report.Sources;
        if (!sources.CommissionsConnected)
        {
            yield return _l["Source.Commissions"];
        }

        if (!sources.InvoicingConnected)
        {
            yield return _l["Source.Invoicing"];
        }

        if (!sources.ChargesConnected)
        {
            yield return _l["Source.Charges"];
        }

        yield return _l["Source.Costs", string.Join(", ", sources.CostSources.Select(CostSourceName))];
        yield return _l["Source.Revenue"];
        yield return _l["Source.Deposits"];
    }

    private string CostSourceName(string name)
    {
        var text = _l[$"Source.Cost.{name}"];
        return text.ResourceNotFound ? name : text.Value;
    }

    private void Daily(IXLWorksheet sheet)
    {
        var writer = new SheetWriter(sheet);
        writer.Header(FleetColumns([(_l["Column.Day"], Kind.Date)]));
        foreach (var day in _report.Daily)
        {
            writer.Row(FleetValues(day.Totals, day.Day));
        }

        writer.EndData();
        writer.Total(FleetValues(_report.Fleet, _l["Table.Total"].Value));
        writer.Finish();
    }

    private void Vehicles(IXLWorksheet sheet)
    {
        var writer = new SheetWriter(sheet);
        writer.Header(
        [
            .. FleetColumns([(_l["Column.Plate"], Kind.Text), (_l["Column.Vehicle"], Kind.Text), (_l["Column.Category"], Kind.Text)]),
            (_l["Column.HintMargin", _report.SellHintMonths], Kind.Amount),
            (_l["Column.HintDays"], Kind.Count),
            (_l["Column.Hint"], Kind.Text),
        ]);
        foreach (var vehicle in _report.Vehicles)
        {
            writer.Row(
            [
                .. FleetValues(vehicle.Totals, vehicle.PlateNumber, vehicle.VehicleName, vehicle.CategoryCode),
                vehicle.HintDays > 0 ? vehicle.HintMargin : null,
                vehicle.HintDays,
                vehicle.SuggestSaleOrReplacement ? _l["Vehicles.Hint"].Value : null,
            ]);
        }

        writer.EndData();
        writer.Total([.. FleetValues(_report.Fleet, _l["Table.Total"].Value, null, null), null, null, null]);
        writer.Finish();
    }

    /// <summary>The columns of fleet totals after the <paramref name="leading"/> ones (days, vehicles).</summary>
    private (string, Kind)[] FleetColumns((string, Kind)[] leading) =>
    [
        .. leading,
        (_l["Column.VehicleDays"], Kind.Count),
        (_l["Column.AvailableDays"], Kind.Count),
        (_l["Column.RentedDays"], Kind.Count),
        (_l["Column.Utilization"], Kind.Ratio),
        (_l["Column.Revenue"], Kind.Amount),
        (_l["Column.Commission"], Kind.Amount),
        (_l["Column.DirectCosts"], Kind.Amount),
        (_l["Column.Depreciation"], Kind.Amount),
        (_l["Column.Margin"], Kind.Amount),
        (_l["Column.RevPav"], Kind.Amount),
        (_l["Column.AverageRentedDayPrice"], Kind.Amount),
    ];

    private static object?[] FleetValues(FleetDayTotals totals, params object?[] leading) =>
    [
        .. leading,
        totals.VehicleDays,
        totals.AvailableDays,
        totals.RentedDays,
        totals.Utilization,
        totals.Revenue,
        totals.Commission,
        totals.DirectCosts,
        totals.Depreciation,
        totals.Margin,
        totals.RevenuePerAvailableDay,
        totals.AverageRentedDayPrice,
    ];

    private void Channels(IXLWorksheet sheet)
    {
        var report = _report.Channels;
        var writer = new SheetWriter(sheet);
        writer.Header(
            (_l["Column.Channel"], Kind.Text),
            (_l["Column.Bookings"], Kind.Count),
            (_l["Column.RentedDays"], Kind.Count),
            (_l["Column.Revenue"], Kind.Amount),
            (_l["Column.RevenueShare"], Kind.Ratio),
            (_l["Column.Commission"], Kind.Amount),
            (_l["Column.NetRevenue"], Kind.Amount),
            (_l["Column.AllocatedCosts"], Kind.Amount),
            (_l["Column.Margin"], Kind.Amount),
            (_l["Column.AverageRentedDayPrice"], Kind.Amount));
        foreach (var channel in report.Channels)
        {
            writer.Row(
                _l[$"Channel.{channel.Channel}"].Value,
                channel.Bookings,
                channel.RentedDays,
                channel.Revenue,
                report.ShareOf(channel),
                channel.Commission,
                channel.NetRevenue,
                channel.AllocatedCosts,
                channel.Margin,
                channel.AverageRentedDayPrice);
        }

        writer.EndData();
        writer.Row(_l["Channels.Idle"].Value, null, null, null, null, null, null, report.IdleCosts, -report.IdleCosts, null);
        writer.Total(_l["Table.Total"].Value, report.Channels.Sum(c => c.Bookings), report.Channels.Sum(c => c.RentedDays), report.Revenue, null, report.Commission, report.Revenue - report.Commission, report.Channels.Select(c => c.AllocatedCosts).Sum() + report.IdleCosts, report.Margin, null);
        if (report.UnattributedRevenue.IsPositive)
        {
            writer.Skip();
            writer.Note(_l["Channels.Unattributed", ReportFormat.Amount(report.UnattributedRevenue)]);
        }

        writer.Finish();
    }

    private void Pace(IXLWorksheet sheet)
    {
        var pace = _report.Pace;
        var writer = new SheetWriter(sheet);
        writer.Header(
            (_l["Column.Category"], Kind.Text),
            (_l["Column.Horizon"], Kind.Count),
            (_l["Column.SoldDays"], Kind.Count),
            (_l["Column.TentativeDays"], Kind.Count),
            (_l["Column.CapacityDays"], Kind.Count),
            (_l["Column.Occupancy"], Kind.Ratio),
            (_l["Column.OccupancyWithTentative"], Kind.Ratio),
            (_l["Column.LastYearSoldDays"], Kind.Count),
            (_l["Column.ChangeOnLastYear"], Kind.Ratio));
        foreach (var category in pace.Categories)
        {
            foreach (var horizon in category.Horizons)
            {
                writer.Row(PaceValues(category.CategoryCode, horizon));
            }
        }

        writer.EndData();
        foreach (var horizon in pace.Totals)
        {
            writer.Total(PaceValues(_l["Pace.Total"].Value, horizon));
        }

        writer.Skip();
        writer.Note(_l["Pace.AsOf", ReportFormat.Date(pace.AsOf)]);
        writer.Finish();
    }

    private static object?[] PaceValues(string category, PaceHorizon horizon) =>
    [
        category,
        horizon.Days,
        horizon.SoldDays,
        horizon.TentativeDays,
        horizon.CapacityDays,
        horizon.Occupancy,
        horizon.OccupancyWithTentative,
        horizon.LastYearSoldDays,
        horizon.ChangeOnLastYear,
    ];

    private void Funnel(IXLWorksheet sheet)
    {
        var funnel = _report.Funnel;
        var writer = new SheetWriter(sheet);
        writer.Header(
            (_l["Column.Channel"], Kind.Text),
            (_l["Column.Source"], Kind.Text),
            (_l["Column.Searches"], Kind.Count),
            (_l["Column.Results"], Kind.Count),
            (_l["Column.Requests"], Kind.Count),
            (_l["Column.Confirmed"], Kind.Count),
            (_l["Column.SearchToRequest"], Kind.Ratio),
            (_l["Column.RequestToConfirmed"], Kind.Ratio),
            (_l["Column.SearchToConfirmed"], Kind.Ratio));
        foreach (var row in funnel.Rows)
        {
            writer.Row(FunnelValues(row, row.Channel is { } channel ? _l[$"Channel.{channel}"].Value : string.Empty, row.Source ?? _l["Funnel.Direct"].Value));
        }

        writer.EndData();
        foreach (var channel in funnel.Channels)
        {
            writer.Subtotal(FunnelValues(funnel.TotalOf(channel), _l["Funnel.ChannelTotal", _l[$"Channel.{channel}"].Value].Value, null));
        }

        writer.Total(FunnelValues(funnel.Total, _l["Table.Total"].Value, null));
        writer.Finish();
    }

    private static object?[] FunnelValues(FunnelRow row, string channel, string? source) =>
        [channel, source, row.Searches, row.Results, row.Requests, row.Confirmed, row.SearchToRequest, row.RequestToConfirmed, row.SearchToConfirmed];

    private void Requests(IXLWorksheet sheet)
    {
        var requests = _report.Requests;
        var writer = new SheetWriter(sheet);
        writer.Header(
            (_l["Column.Channel"], Kind.Text),
            (_l["Column.Submitted"], Kind.Count),
            (_l["Column.Validated"], Kind.Count),
            (_l["Column.Refused"], Kind.Count),
            (_l["Column.Expired"], Kind.Count),
            (_l["Column.Cancelled"], Kind.Count),
            (_l["Column.Pending"], Kind.Count),
            (_l["Column.RefusalRate"], Kind.Ratio),
            (_l["Column.ExpiryRate"], Kind.Ratio),
            (_l["Column.MedianBusinessDelay"], Kind.Duration),
            (_l["Column.MedianDelay"], Kind.Duration),
            (_l["Column.WithinTarget", ReportFormat.Duration(requests.Target)], Kind.Count),
            (_l["Column.WithinTargetRate"], Kind.Ratio));
        foreach (var row in requests.Channels)
        {
            writer.Row(RequestValues(row, row.Channel is { } channel ? _l[$"Channel.{channel}"].Value : string.Empty));
        }

        writer.EndData();
        writer.Total(RequestValues(requests.Total, _l["Table.Total"].Value));
        writer.Finish();
    }

    private static object?[] RequestValues(RequestQualityRow row, string channel) =>
    [
        channel,
        row.Submitted,
        row.Validated,
        row.Refused,
        row.Expired,
        row.Cancelled,
        row.Pending,
        row.RefusalRate,
        row.ExpiryRate,
        row.MedianBusinessDelay,
        row.MedianDelay,
        row.DecidedWithinTarget,
        row.WithinTargetRate,
    ];

    private void Cancellations(IXLWorksheet sheet)
    {
        var cancellations = _report.Cancellations;
        var writer = new SheetWriter(sheet);
        writer.Header(
            (_l["Column.Channel"], Kind.Text),
            (_l["Column.FirmBookings"], Kind.Count),
            (_l["Column.CancelledFirm"], Kind.Count),
            (_l["Column.CancellationRate"], Kind.Ratio),
            (_l["Column.NoShows"], Kind.Count),
            (_l["Column.NoShowRate"], Kind.Ratio),
            (_l["Column.CancelledBeforeConfirmation"], Kind.Count),
            (_l["Column.Fees"], Kind.Amount));
        foreach (var row in cancellations.Channels)
        {
            writer.Row(CancellationValues(row, row.Channel is { } channel ? _l[$"Channel.{channel}"].Value : string.Empty));
        }

        writer.EndData();
        writer.Total(CancellationValues(cancellations.Total, _l["Table.Total"].Value));
        writer.Finish();
    }

    private static object?[] CancellationValues(CancellationRow row, string channel) =>
        [channel, row.FirmBookings, row.Cancelled, row.CancellationRate, row.NoShows, row.NoShowRate, row.CancelledBeforeConfirmation, row.Fees];

    private void Cash(IXLWorksheet sheet)
    {
        var cash = _report.Cash;
        var writer = new SheetWriter(sheet);
        writer.Title(_l["Cash.Collected"]);
        writer.Header(
            (_l["Column.Method"], Kind.Text),
            (_l["Column.Received"], Kind.Amount),
            (_l["Column.Refunded"], Kind.Amount),
            (_l["Column.Net"], Kind.Amount),
            (_l["Column.Payments"], Kind.Count),
            (_l["Column.Refunds"], Kind.Count));
        foreach (var method in cash.Collected)
        {
            writer.Row(_l[$"Method.{method.Method}"].Value, method.Received, method.Refunded, method.Net, method.Payments, method.Refunds);
        }

        writer.Total(
            _l["Table.Total"].Value,
            cash.Collected.Select(c => c.Received).Sum(),
            cash.Collected.Select(c => c.Refunded).Sum(),
            cash.CollectedNet,
            cash.Collected.Sum(c => c.Payments),
            cash.Collected.Sum(c => c.Refunds));
        writer.Pair(_l["Cash.PreviousNet"], cash.PreviousCollectedNet, Kind.Amount);
        writer.Skip();

        writer.Title(_l["Cash.Invoiced"]);
        if (_report.Sources.InvoicingConnected)
        {
            writer.Header(
                (_l["Column.InvoicedExclVat"], Kind.Amount),
                (_l["Column.InvoicedVat"], Kind.Amount),
                (_l["Column.InvoicedInclVat"], Kind.Amount),
                (_l["Column.Invoices"], Kind.Count),
                (_l["Column.CreditNotes"], Kind.Count));
            writer.Row(cash.Invoiced.ExclVat, cash.Invoiced.Vat, cash.Invoiced.InclVat, cash.Invoiced.InvoiceCount, cash.Invoiced.CreditNoteCount);
        }
        else
        {
            writer.Note(_l["Source.Invoicing"]);
        }

        writer.Skip();
        writer.Title(_l["Cash.Deposits"]);
        writer.Header((_l["Column.DepositMode"], Kind.Text), (_l["Column.Amount"], Kind.Amount), (_l["Column.Count"], Kind.Count));
        foreach (var mode in cash.DepositsByMode)
        {
            writer.Row(_l[$"DepositMode.{mode.Mode}"].Value, mode.Total, mode.Count);
        }

        writer.Total(_l["Table.Total"].Value, cash.OpenDeposits, cash.OpenDepositCount);
        writer.Note(_l["Source.Deposits"]);
        writer.Finish(filter: false);
    }

    private void Unpaid(IXLWorksheet sheet)
    {
        var cash = _report.Cash;
        var writer = new SheetWriter(sheet);
        writer.Header(
            (_l["Column.Booking"], Kind.Text),
            (_l["Column.Channel"], Kind.Text),
            (_l["Column.ReturnedOn"], Kind.Date),
            (_l["Column.Due"], Kind.Amount),
            (_l["Column.NetPaid"], Kind.Amount),
            (_l["Column.Balance"], Kind.Amount));
        foreach (var unpaid in cash.Unpaid)
        {
            writer.Row(unpaid.Number, _l[$"Channel.{unpaid.Channel}"].Value, unpaid.ReturnedOn, unpaid.Due, unpaid.NetPaid, unpaid.Balance);
        }

        writer.EndData();
        writer.Total(_l["Table.Total"].Value, null, null, cash.Unpaid.Select(u => u.Due).Sum(), cash.Unpaid.Select(u => u.NetPaid).Sum(), cash.UnpaidTotal);
        writer.Finish();
    }

    private string Range(DateOnly from, DateOnly to) => _l["Period.Range", ReportFormat.Date(from), ReportFormat.Date(to)];

    /// <summary>How a column shows its values.</summary>
    private enum Kind
    {
        Text = 0,
        Count = 1,
        Amount = 2,
        Ratio = 3,
        Date = 4,
        Duration = 5,
        Instant = 6,
    }

    /// <summary>A ratio written in a text column (summary sheet): shown as a percentage.</summary>
    private readonly record struct Ratio(decimal Value);

    /// <summary>Writes the rows of a sheet from its first cell, remembering the kinds of its columns and their widths.</summary>
    private sealed class SheetWriter(IXLWorksheet sheet)
    {
        private readonly Dictionary<int, int> _widths = [];
        private Kind[] _kinds = [];
        private int _row;
        private int _headerRow;
        private int _lastDataRow;

        public void Title(string text)
        {
            _row++;
            var cell = sheet.Cell(_row, 1);
            cell.Value = text;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontSize = 12;
        }

        public void Note(string text)
        {
            _row++;
            sheet.Cell(_row, 1).Value = text;
        }

        public void Skip() => _row++;

        /// <summary>A label in the first column and its value in the second.</summary>
        public void Pair(string label, object? value, Kind kind = Kind.Text)
        {
            _row++;
            var cell = sheet.Cell(_row, 1);
            cell.Value = label;
            cell.Style.Font.Bold = true;
            Track(1, label);
            Write(sheet.Cell(_row, 2), value, kind, 2);
        }

        public void Header(params (string Title, Kind Kind)[] columns)
        {
            _row++;
            _headerRow = _row;
            _lastDataRow = 0;
            _kinds = [.. columns.Select(c => c.Kind)];
            for (var i = 0; i < columns.Length; i++)
            {
                var cell = sheet.Cell(_row, i + 1);
                cell.Value = columns[i].Title;
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = HeaderFill;
                cell.Style.Alignment.WrapText = true;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
                cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                Track(i + 1, columns[i].Title, header: true);
            }
        }

        public void Row(params object?[] values)
        {
            _row++;
            for (var i = 0; i < values.Length; i++)
            {
                Write(sheet.Cell(_row, i + 1), values[i], i < _kinds.Length ? _kinds[i] : Kind.Text, i + 1);
            }
        }

        /// <summary>The data rows end here: the filter covers the header and the rows written so far.</summary>
        public void EndData() => _lastDataRow = _row;

        public void Subtotal(params object?[] values)
        {
            Row(values);
            var range = sheet.Range(_row, 1, _row, Math.Max(1, values.Length));
            range.Style.Font.Bold = true;
            range.Style.Fill.BackgroundColor = SubtotalFill;
        }

        public void Total(params object?[] values)
        {
            Row(values);
            var range = sheet.Range(_row, 1, _row, Math.Max(1, values.Length));
            range.Style.Font.Bold = true;
            range.Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        public void Finish(bool filter = true)
        {
            if (filter && _headerRow > 0)
            {
                sheet.SheetView.FreezeRows(_headerRow);
                sheet.Range(_headerRow, 1, Math.Max(_headerRow, _lastDataRow), Math.Max(1, _kinds.Length)).SetAutoFilter();
            }

            foreach (var (column, width) in _widths)
            {
                sheet.Column(column).Width = width;
            }
        }

        private void Write(IXLCell cell, object? value, Kind kind, int column)
        {
            switch (value)
            {
                case null:
                    cell.Value = Blank.Value;
                    break;
                case Money money:
                    cell.Value = money.Amount;
                    cell.Style.NumberFormat.Format = AmountFormat;
                    Track(column, money.Amount.ToString("N3", CultureInfo.InvariantCulture));
                    break;
                case Ratio ratio:
                    cell.Value = ratio.Value;
                    cell.Style.NumberFormat.Format = RatioFormat;
                    Track(column, "100.0%");
                    break;
                case decimal number:
                    cell.Value = number;
                    cell.Style.NumberFormat.Format = kind == Kind.Ratio ? RatioFormat : AmountFormat;
                    Track(column, kind == Kind.Ratio ? "100.0%" : number.ToString("N3", CultureInfo.InvariantCulture));
                    break;
                case int count:
                    cell.Value = count;
                    cell.Style.NumberFormat.Format = CountFormat;
                    Track(column, count.ToString("N0", CultureInfo.InvariantCulture));
                    break;
                case DateOnly day:
                    cell.Value = day.ToDateTime(TimeOnly.MinValue);
                    cell.Style.NumberFormat.NumberFormatId = ShortDateFormatId;
                    Track(column, "2026-10-31");
                    break;
                case DateTime instant:
                    cell.Value = instant;
                    cell.Style.NumberFormat.Format = InstantFormat;
                    Track(column, "2026-10-31 23:59");
                    break;
                case TimeSpan duration:
                    cell.Value = duration;
                    cell.Style.NumberFormat.Format = DurationFormat;
                    Track(column, "999:59");
                    break;
                default:
                    var text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
                    cell.Value = text;
                    Track(column, text);
                    break;
            }
        }

        /// <summary>Widens <paramref name="column"/> to its longest text (headers wrap beyond 24 characters).</summary>
        private void Track(int column, string text, bool header = false)
        {
            var length = header ? Math.Min(text.Length, 24) : text.Length;
            var width = Math.Clamp(length + 2, 8, 60);
            _widths[column] = Math.Max(_widths.GetValueOrDefault(column), width);
        }
    }
}
