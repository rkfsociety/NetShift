namespace NetShift.App.Services;

internal static class LegacyDnsScriptFragments
{
    public const string CaptureBackupOnlyWhenInactive = "if([int]$backup.GetValue('Enabled',0) -ne 1){ ";
    public const string FindAdapterByGuid = "$a=Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object { ([guid]$_.InterfaceGuid) -eq $guid } | Select-Object -First 1; ";
    public const string RequireAdapterBeforeRestore = "if($null -eq $a){throw 'Адаптер Legacy DNS пока не найден; резервная копия сохранена.'}; ";

    public const string RestoreDnsSettings =
        "Set-DnsClientServerAddress -InterfaceIndex $a.InterfaceIndex -ResetServerAddresses; " +
        "if(-not $dhcp4 -and $d4.Count -gt 0){Set-DnsClientServerAddress -InterfaceIndex $a.InterfaceIndex -ServerAddresses $d4}; " +
        "if(-not $dhcp6 -and $d6.Count -gt 0){Set-DnsClientServerAddress -InterfaceIndex $a.InterfaceIndex -ServerAddresses $d6}; ";
}
