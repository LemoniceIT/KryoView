param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
try {
    if (-not ('System.Formats.Nrbf.NrbfDecoder' -as [type])) {
        $runtime = Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet/shared/Microsoft.WindowsDesktop.App') -Directory -ErrorAction SilentlyContinue |
            Where-Object { (Test-Path (Join-Path $_.FullName 'System.Formats.Nrbf.dll')) -and ([version]($_.Name -split '-')[0]).Major -le [Environment]::Version.Major } |
            Sort-Object { [version]($_.Name -split '-')[0] } -Descending | Select-Object -First 1
        if (!$runtime) { throw 'Install a recent PowerShell 7 and .NET Desktop Runtime with System.Formats.Nrbf.' }
        Add-Type -Path (Join-Path $runtime.FullName 'System.Formats.Nrbf.dll')
    }
    function Member($record, [string]$name) {
        if ($null -eq $record) { return }
        foreach ($key in @("<$name>k__BackingField", "GraphItem+<$name>k__BackingField", $name)) {
            if ($record.HasMember($key)) { return $record.GetRawValue($key) }
        }
    }
    function Color($record, [string]$fallback) {
        $value = Member $record 'value'
        $state = Member $record 'state'
        if ($state -band 2) { return ('{0:X8}' -f ($value -band 4294967295L)) }
        $known = Member $record 'knownColor'
        if ($known) { return ('{0:X8}' -f [Drawing.Color]::FromKnownColor([Drawing.KnownColor]$known).ToArgb()) }
        return $fallback
    }
    function Attr([string]$name, $value) {
        if ($null -ne $value) { $writer.WriteAttributeString($name, [string]$value) }
    }

    $inputFile = Get-Item -LiteralPath $Source
    if ($inputFile.Length -gt 32MB) { throw 'Theme exceeds the 32 MiB import limit.' }
    $inputStream = $inputFile.OpenRead()
    try { $root = [System.Formats.Nrbf.NrbfDecoder]::Decode($inputStream, $null, $true) }
    finally { $inputStream.Dispose() }
    if ($root.TypeName.FullName -ne 'UsbMonitorL.Theme') { throw 'This file is not a supported TURZX theme.' }
    $width = Member $root 'width'
    $height = Member $root 'height'
    if ("$width x $height" -notin '480 x 1920','1920 x 480') { throw 'Select an 8.8-inch theme.' }
    $list = $root.GetClassRecord('<GraphList>k__BackingField').GetClassRecord('Collection`1+items')
    $count = $list.GetInt32('_size')
    if ($count -lt 1 -or $count -gt 256) { throw 'Unsupported widget count.' }
    $widgets = $list.GetArrayRecord('_items').GetArray($true)
    if ($count -gt $widgets.Length) { throw 'The widget list is incomplete.' }
    $folder = [IO.Directory]::CreateDirectory($Destination).FullName
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $temporaryLayout = Join-Path $folder 'layout.xml.tmp'
    $writer = [Xml.XmlWriter]::Create($temporaryLayout, $settings)
    try {
        $writer.WriteStartElement('theme')
        Attr 'name' $inputFile.BaseName
        Attr 'width' $width
        Attr 'height' $height
        for ($i = 0; $i -lt $count; $i++) {
            $widget = $widgets[$i]
            if (!$widget -or (Member $widget 'hide')) { continue }
            $type = Member $widget 'TypeName'
            if ($type -notin 'Image','Text','Data','ArchBar','StatuBar') { Write-Output "Skipped widget type: $type"; continue }
            $asset = $null
            if ($type -eq 'Image') {
                $bitmap = Member $widget 'bitmap'
                if (!$bitmap -or $bitmap.TypeName.FullName -ne 'System.Drawing.Bitmap') { continue }
                $array = $bitmap.GetArrayRecord('Data')
                
                $bytes = $array.GetArray([byte[]], $false)
                if ($bytes.Length -lt 24 -or [BitConverter]::ToString($bytes,0,8) -ne '89-50-4E-47-0D-0A-1A-0A') { continue }
                $w = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes,16))
                $h = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes,20))
                if ($w -lt 1 -or $h -lt 1 -or $w -gt 4096 -or $h -gt 4096) { throw 'Unsupported image dimensions.' }
                $asset = "asset-$i.png"
                [IO.File]::WriteAllBytes((Join-Path $folder $asset),$bytes)
            }
            $writer.WriteStartElement('widget')
            Attr 'type' $type
            Attr 'x' (Member $widget 'posX')
            Attr 'y' (Member $widget 'posY')
            Attr 'file' $asset
            $data = Member $widget 'm_data'
            Attr 'metric' (Member $data 'DataName')
            Attr 'source' (Member $data 'SubName')
            Attr 'unit' (Member $data 'ShowUnit')
            if ($type -eq 'Text') { Attr 'label' (Member $data 'ValueWithUnit') }
            $font = Member $widget 'fontConfig'
            Attr 'font' (Member $font 'name')
            Attr 'size' (Member $font 'size')
            Attr 'bold' (Member $font 'isBold')
            Attr 'align' (Member (Member $font 'alignment') 'index')
            $front = if ($font) { Member $font 'color' } else { Member $widget 'FrontColor' }
            Attr 'color' (Color $front 'FFFFFFFF')
            Attr 'track' (Color (Member $widget 'BackColor') 'FF303038')
            foreach ($key in 'width','height','diameter','archWidth','direction','zoom_rate') { Attr $key (Member $widget $key) }
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
    } finally { $writer.Dispose() }
    Move-Item -LiteralPath $temporaryLayout -Destination (Join-Path $folder 'layout.xml') -Force
    Write-Output "Imported: $($inputFile.BaseName)"
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}

