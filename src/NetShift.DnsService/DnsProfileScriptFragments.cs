namespace NetShift.DnsService;

internal static class DnsProfileScriptFragments
{
    public const string FindAdapterByGuid = "$adapter = Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object { ([guid]$_.InterfaceGuid) -eq $guid } | Select-Object -First 1; ";
}
