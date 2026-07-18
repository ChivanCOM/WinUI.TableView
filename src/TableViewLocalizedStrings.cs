using Microsoft.Windows.ApplicationModel.Resources;
using System;

namespace WinUI.TableView;

/// <summary>
/// Provides localized string resources for the TableView.
/// </summary>
internal partial class TableViewLocalizedStrings
{
    private const string WinUI_TableView = "WinUI.TableView";
#if WINDOWS
    private static readonly ResourceManager _resourceManager = new(); 
#else
    private static readonly ResourceLoader _appResourceLoader = new(WinUI_TableView);
    private static readonly ResourceLoader _defaultResourceLoader = new($"{WinUI_TableView}/{WinUI_TableView}");
#endif

    static TableViewLocalizedStrings()
    {
        BlankFilterValue = GetValue(nameof(BlankFilterValue));
        Cancel = GetValue(nameof(Cancel));
        ClearFilter = GetValue(nameof(ClearFilter));
        ClearSorting = GetValue(nameof(ClearSorting));
        Copy = GetValue(nameof(Copy));
        CopyCommandDescription = GetValue(nameof(CopyCommandDescription));
        Paste = GetValue(nameof(Paste));
        PasteCommandDescription = GetValue(nameof(PasteCommandDescription));
        CopyWithHeaders = GetValue(nameof(CopyWithHeaders));
        CopyWithHeadersCommandDescription = GetValue(nameof(CopyWithHeadersCommandDescription));
        DatePickerPlaceholder = GetValue(nameof(DatePickerPlaceholder));
        DeselectAll = GetValue(nameof(DeselectAll));
        DeselectAllCommandDescription = GetValue(nameof(DeselectAllCommandDescription));
        ExportAll = GetValue(nameof(ExportAll));
        ExportSelected = GetValue(nameof(ExportSelected));
        Ok = GetValue(nameof(Ok));
        SearchBoxPlaceholder = GetValue(nameof(SearchBoxPlaceholder));
        SelectAll = GetValue(nameof(SelectAll));
        SelectAllCommandDescription = GetValue(nameof(SelectAllCommandDescription));
        SelectAllParenthesized = GetValue(nameof(SelectAllParenthesized));
        SortAscending = GetValue(nameof(SortAscending));
        SortDescending = GetValue(nameof(SortDescending));
        TimePickerPlaceholder = GetValue(nameof(TimePickerPlaceholder));
        Filtered = GetValue(nameof(Filtered));
    }

    private static string GetValue(string name)
    {
#if WINDOWS
        var value = _resourceManager.MainResourceMap.TryGetValue($"{WinUI_TableView}/{name}");
        value ??= _resourceManager.MainResourceMap.GetValue($"{WinUI_TableView}/{WinUI_TableView}/{name}");

        return value.ValueAsString; 
#else
        if (_appResourceLoader.GetString(name) is { Length: > 0 } appValue)
        {
            return appValue;
        }
        else if (_defaultResourceLoader.GetString(name) is { Length: > 0 } defaultValue)
        {
            return defaultValue;
        }
        // On some hosts (Uno WASM) the packaged .resw for this library isn't resolvable through
        // ResourceLoader. A missing chrome string must never hard-crash layout — fall back to English.
        return Fallback(name);
#endif
    }

    private static string Fallback(string name) => name switch
    {
        nameof(BlankFilterValue) => "(Blank)",
        nameof(Cancel) => "Cancel",
        nameof(ClearFilter) => "Clear Filter",
        nameof(ClearSorting) => "Clear Sorting",
        nameof(Copy) => "Copy",
        nameof(CopyCommandDescription) => "Copy the selected rows to the clipboard",
        nameof(Paste) => "Paste",
        nameof(PasteCommandDescription) => "Paste from the clipboard",
        nameof(CopyWithHeaders) => "Copy with Headers",
        nameof(CopyWithHeadersCommandDescription) => "Copy the selected rows with headers to the clipboard",
        nameof(DatePickerPlaceholder) => "Select date",
        nameof(DeselectAll) => "Deselect All",
        nameof(DeselectAllCommandDescription) => "Deselect all rows",
        nameof(ExportAll) => "Export All",
        nameof(ExportSelected) => "Export Selected",
        nameof(Ok) => "OK",
        nameof(SearchBoxPlaceholder) => "Search",
        nameof(SelectAll) => "Select All",
        nameof(SelectAllCommandDescription) => "Select all rows",
        nameof(SelectAllParenthesized) => "(Select All)",
        nameof(SortAscending) => "Sort Ascending",
        nameof(SortDescending) => "Sort Descending",
        nameof(TimePickerPlaceholder) => "Select time",
        nameof(Filtered) => "Filtered",
        _ => name,
    };

    public static string BlankFilterValue { get; set; }
    public static string Cancel { get; set; }
    public static string ClearFilter { get; set; }
    public static string ClearSorting { get; set; }
    public static string Copy { get; set; }
    public static string CopyCommandDescription { get; set; }
    public static string Paste { get; set; }
    public static string PasteCommandDescription { get; set; }
    public static string CopyWithHeaders { get; set; }
    public static string CopyWithHeadersCommandDescription { get; set; }
    public static string DatePickerPlaceholder { get; set; }
    public static string DeselectAll { get; set; }
    public static string DeselectAllCommandDescription { get; set; }
    public static string ExportAll { get; set; }
    public static string ExportSelected { get; set; }
    public static string Ok { get; set; }
    public static string SearchBoxPlaceholder { get; set; }
    public static string SelectAll { get; set; }
    public static string SelectAllCommandDescription { get; set; }
    public static string SelectAllParenthesized { get; set; }
    public static string SortAscending { get; set; }
    public static string SortDescending { get; set; }
    public static string TimePickerPlaceholder { get; set; }
    public static string Filtered { get; set; }
}
