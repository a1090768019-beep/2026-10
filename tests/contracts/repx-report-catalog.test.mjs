import test from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';

const root = process.cwd();
const reportDir = path.join(root, 'apps/BusinessERP.Desktop/Reports/Legacy');

test('legacy REPX layouts remain preserved and the catalog does not execute report code', async () => {
  const files = (await readdir(reportDir)).filter(name => name.toLowerCase().endsWith('.repx'));
  assert.equal(files.length, 15, 'All individually uploaded REPX layouts must remain in the repository');

  for (const file of files) {
    const xml = await readFile(path.join(reportDir, file), 'utf8');
    assert.match(xml, /<XtraReportsLayoutSerializer\b/);
    assert.doesNotMatch(xml, /<!DOCTYPE\s/i, `DTD must not be present in ${file}`);
  }

  const service = await readFile(path.join(root, 'apps/BusinessERP.Desktop/Services/LegacyReportCatalogService.cs'), 'utf8');
  const importer = await readFile(path.join(root, 'tools/Import-DevExpressReports.ps1'), 'utf8');
  const view = await readFile(path.join(root, 'apps/BusinessERP.Desktop/Views/ReportsWorkspaceControl.xaml'), 'utf8');
  assert.match(service, /DtdProcessing = DtdProcessing\.Prohibit/);
  assert.match(service, /XmlResolver = null/);
  assert.match(service, /LegacyReportTemplate/);
  assert.match(importer, /ZipFile\]::OpenRead/);
  assert.match(importer, /DtdProcessing\]::Prohibit/);
  assert.match(view, /قوالب REPX القديمة/);
  assert.match(view, /LegacyReportTemplates/);
});
