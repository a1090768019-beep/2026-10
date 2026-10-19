[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ArchivePath,
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archiveFullPath = (Resolve-Path -LiteralPath $ArchivePath).Path
$targetDirectory = Join-Path $ProjectRoot 'apps/BusinessERP.Desktop/Reports/Legacy'
$catalogPath = Join-Path $targetDirectory 'catalog.json'
[System.IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
$archive = [System.IO.Compression.ZipFile]::OpenRead($archiveFullPath)

try {
    $entries = @($archive.Entries | Where-Object { [IO.Path]::GetExtension($_.FullName) -ieq '.repx' })
    if ($entries.Count -eq 0) {
        throw "لم يتم العثور على ملفات .repx داخل الأرشيف: $archiveFullPath"
    }

    $catalog = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $entries) {
        if ([IO.Path]::GetFileName($entry.FullName) -ne $entry.FullName) {
            throw "الأرشيف يحتوي مسارًا داخليًا غير متوقع: $($entry.FullName)"
        }

        $stream = $entry.Open()
        $memory = [IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            $bytes = $memory.ToArray()
            $memory.Position = 0
            $settings = [System.Xml.XmlReaderSettings]::new()
            $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $settings.MaxCharactersInDocument = 10000000
            $reader = [System.Xml.XmlReader]::Create($memory, $settings)
            try {
                $xml = [System.Xml.XmlDocument]::new()
                $xml.XmlResolver = $null
                $xml.Load($reader)
            }
            finally {
                $reader.Dispose()
            }

            if ($xml.DocumentElement.LocalName -ne 'XtraReportsLayoutSerializer') {
                throw "صيغة REPX غير معروفة في $($entry.Name)"
            }

            [IO.File]::WriteAllBytes((Join-Path $targetDirectory $entry.Name), $bytes)
        }
        finally {
            $stream.Dispose()
            $memory.Dispose()
        }

        $root = $xml.DocumentElement
        $fieldSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($node in $xml.SelectNodes('//*[@Expression]')) {
            foreach ($match in [regex]::Matches($node.GetAttribute('Expression'), '\[([A-Za-z_][\w.]*)\]')) {
                $field = $match.Groups[1].Value
                if (-not $field.StartsWith('DataSource.', [StringComparison]::OrdinalIgnoreCase)) {
                    [void]$fieldSet.Add($field)
                }
            }
        }

        $name = [IO.Path]::GetFileNameWithoutExtension($entry.Name)
        $category = switch -Regex ($name) {
            'POS|Barcode|daysalesPos|InvDetailsPos|InvSumtPos|PurchDetailsPos' { 'نقطة البيع والفواتير'; break }
            'Tax|Vat' { 'الضريبة'; break }
            'Cust|Client|Supplier|Account|Statement' { 'العملاء والحسابات'; break }
            'Purch|Purchase' { 'المشتريات'; break }
            'Inv|Item|Inventory|Serial|Production|Pricing' { 'المخزون والأصناف'; break }
            'Sales|Sale|NetSales|daysales' { 'المبيعات'; break }
            'Safe|Khzna|Bond|Payment|Receipt|Close|Shift|Entry|Daily|SummaryD' { 'الخزائن والسندات'; break }
            'Income|Mezan|CostCenter|Balance|Summary' { 'التقارير المالية'; break }
            default { 'عام' }
        }

        $catalog.Add([pscustomobject]@{
            fileName = $entry.Name
            category = $category
            serializerVersion = [string]$root.GetAttribute('SerializerVersion')
            reportVersion = [string]$root.GetAttribute('Version')
            paperKind = [string]$root.GetAttribute('PaperKind')
            pageWidth = [string]$root.GetAttribute('PageWidth')
            pageHeight = [string]$root.GetAttribute('PageHeight')
            controlCount = $xml.SelectNodes('//*[@ControlType]').Count
            boundFieldCount = $fieldSet.Count
            boundFields = @($fieldSet | Sort-Object)
            integrationStatus = 'ImportedLayoutOnly'
        })
    }

    $catalog | Sort-Object fileName | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $catalogPath -Encoding UTF8

    Write-Host "تم استيراد $($entries.Count) قالب REPX إلى: $targetDirectory" -ForegroundColor Green
    Write-Host "تم إنشاء الفهرس: $catalogPath" -ForegroundColor Green
    Write-Host 'تنبيه: حفظ القوالب لا يعني اكتمال ربط البيانات أو توفر محرك DevExpress وقت التشغيل.'
}
finally {
    $archive.Dispose()
}
