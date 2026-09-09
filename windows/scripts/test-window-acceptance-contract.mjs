import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const helper = readFileSync(new URL('./WindowAcceptance.ps1', import.meta.url), 'utf8')
  .replace(/\r\n?/g, '\n');
const startup = readFileSync(new URL('./Test-AppStartup.ps1', import.meta.url), 'utf8')
  .replace(/\r\n?/g, '\n');
const msix = readFileSync(new URL('./Test-MsixInstallation.ps1', import.meta.url), 'utf8')
  .replace(/\r\n?/g, '\n');

test('native acceptance rejects windows that are hidden, minimized, cloaked, capture-protected, empty, or off-monitor', () => {
  for (const required of [
    'IsWindowVisible',
    'IsIconic',
    'GetWindowRect',
    'EnumDisplayMonitors',
    'GetMonitorInfo',
    'DwmGetWindowAttribute',
    'DwmWindowAttributeCloaked = 14',
    'GetWindowDisplayAffinity',
    'WindowDisplayAffinityNone = 0',
    'PositiveSize',
    'IntersectsMonitorWorkArea',
  ]) assert.ok(helper.includes(required), `Missing native acceptance guard: ${required}`);

  assert.match(helper, /if \(\$nativeState\.DisplayAffinity -ne \[Crosio\.WindowAcceptance\.NativeWindow\]::DisplayAffinityNone\)/);
});

test('UI Automation acceptance requires a restored root and visible enabled home content', () => {
  for (const required of [
    'UIAutomationClient',
    'AutomationElement]::FromHandle',
    'WindowPattern]::Pattern',
    'WindowVisualState]::Minimized',
    'AutomationElement]::NameProperty',
    'InvokePattern]::Pattern.Id',
    'SelectionItemPattern]::Pattern.Id',
    'homeCurrent.IsEnabled',
    'homeCurrent.IsOffscreen',
    'homeBounds.Width -gt 0',
    'homeBounds.Height -gt 0',
  ]) assert.ok(helper.includes(required), `Missing UI Automation acceptance guard: ${required}`);
});

test('both launch paths use the shared interactive window assessment', () => {
  for (const [name, script] of [['unpackaged', startup], ['MSIX', msix]]) {
    assert.ok(script.includes('. $windowAcceptancePath'), `${name} launch does not load the shared helper.`);
    assert.ok(script.includes('Get-OnePawInteractiveWindowAssessment'),
      `${name} launch does not require interactive-window acceptance.`);
    assert.ok(script.includes('$expectedHomeName = -join @([char]0x9996, [char]0x9875)'),
      `${name} launch does not check the home UI Automation element.`);
  }
});

test('packaged acceptance hides and reactivates the resident primary process', () => {
  for (const required of [
    'HideWindow($mainWindow)',
    'StartRedirect(',
    '$activatedProcessId)',
    'FindMainWindow($activatedProcessId)',
    '$redirectProcess.WaitForExit(5000)',
    'Resident-process reactivation restored the main window.',
    'Redirect activation process cleanup',
  ]) assert.ok(msix.includes(required), `Missing resident reactivation contract: ${required}`);

  assert.match(msix, /if \(returnedProcessId == acceptedExistingProcessId\)/);
  assert.match(msix, /if \(\$startedProcess\.HasExited\)[\s\S]*original installed Crosio process exited during redirected activation/);
});
