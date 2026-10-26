# SPDX-License-Identifier: GPL-2.0-or-later
# Check the graphical installer's OS-language default, manual switch and phase labels without installing.
param([Parameter(Mandatory=$true)][string]$Setup)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Setup).Path)
$type = $assembly.GetType('SetupWindow', $true)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$window = [Activator]::CreateInstance($type, $true)
try {
    $window.Show()
    $expectedIcon = [Drawing.Icon]::ExtractAssociatedIcon($assembly.Location)
    try {
        $actualBitmap = $window.Icon.ToBitmap()
        $expectedBitmap = $expectedIcon.ToBitmap()
        try {
            if ($actualBitmap.Width -ne $expectedBitmap.Width -or
                $actualBitmap.Height -ne $expectedBitmap.Height -or
                $actualBitmap.GetPixel(0, 0).ToArgb() -ne $expectedBitmap.GetPixel(0, 0).ToArgb() -or
                $actualBitmap.GetPixel([int]($actualBitmap.Width / 2), [int]($actualBitmap.Height / 2)).ToArgb() -ne
                    $expectedBitmap.GetPixel([int]($expectedBitmap.Width / 2), [int]($expectedBitmap.Height / 2)).ToArgb()) {
                throw 'Installer window does not use the icon embedded in MESHingSetup.exe.'
            }
        } finally {
            $actualBitmap.Dispose()
            $expectedBitmap.Dispose()
        }
    } finally { $expectedIcon.Dispose() }
    $picker = $type.GetField('languagePicker', $flags).GetValue($window)
    $heading = $type.GetField('heading', $flags).GetValue($window)
    $subtitle = $type.GetField('subtitle', $flags).GetValue($window)
    $introduction = $type.GetField('introduction', $flags).GetValue($window)
    $registration = $type.GetField('registrationNotice', $flags).GetValue($window)
    $license = $type.GetField('licenseNotice', $flags).GetValue($window)
    $steps = $type.GetField('stages', $flags).GetValue($window)
    $progress = $type.GetField('progress', $flags).GetValue($window)
    $phase = $type.GetMethod('SetPhase', $flags)
    $expected = if ([Globalization.CultureInfo]::InstalledUICulture.TwoLetterISOLanguageName -eq 'it') { 0 } else { 1 }
    if ($picker.SelectedIndex -ne $expected) { throw 'The Windows display language was not detected.' }
    $picker.SelectedIndex = 0
    if ($heading.Text -ne 'MESHing' -or $subtitle.Text -ne 'Generazione avanzata di mesh 2D' -or
        $introduction.Visible -or $introduction.Text -or
        $registration.Text -notmatch 'registra MESHing' -or
        $license.Text -notmatch 'Autore: Leonardo Bandini - CSi Italia' -or
        $license.Text -match '©|Copyright' -or
        $license.Text -notmatch 'MESHing: GPL-2.0-or-later.*GPLv3' -or
        $license.Text -notmatch 'Gmsh 4\.15\.2: GPL-2\.0-or-later' -or
        $steps[0].Text -notmatch 'Verifica delle applicazioni') {
        throw 'Manual Italian selection failed.'
    }
    $picker.SelectedIndex = 1
    if ($subtitle.Text -ne 'Advanced 2D mesh generation' -or
        $introduction.Visible -or $introduction.Text -or
        $registration.Text -notmatch 'registers MESHing' -or
        $license.Text -notmatch 'Author: Leonardo Bandini - CSi Italia' -or
        $license.Text -match '©|Copyright' -or
        $license.Text -notmatch 'MESHing: GPL-2.0-or-later.*GPLv3' -or
        $steps[0].Text -notmatch 'Check for open applications') {
        throw 'Manual English selection failed.'
    }
    foreach ($name in @('hosts', 'prerequisite', 'prerequisite-ready', 'verification', 'copy',
        'previous-none', 'registration', 'menus', 'done')) {
        [void]$phase.Invoke($window, [object[]]@($name))
    }
    if ($progress.Value -ne 9 -or $steps[2].Text -notmatch 'Not needed' -or
        $steps[5].Text -notmatch 'Not needed' -or $steps[8].Text -notmatch 'Complete') {
        throw 'Installer did not mark the already-present prerequisite as skipped.'
    }
    $picker.SelectedIndex = 0
    if ($steps[2].Text -notmatch 'Non necessario' -or $steps[5].Text -notmatch 'Non necessario' -or
        $steps[8].Text -notmatch 'Completato') {
        throw 'The phase list did not follow a mid-installation language switch.'
    }
    'GRAPHICAL_INSTALLER=PASS EMBEDDED_ICON=PASS OS_LANGUAGE=DETECTED MANUAL_IT_EN=PASS PHASES=9 PREREQUISITE_PRESENT=SKIPPED PREVIOUS_VERSION=SKIPPED'
} finally {
    $window.Close()
    $window.Dispose()
}
$window = [Activator]::CreateInstance($type, $true)
try {
    $window.Show()
    $picker = $type.GetField('languagePicker', $flags).GetValue($window)
    $steps = $type.GetField('stages', $flags).GetValue($window)
    $status = $type.GetField('status', $flags).GetValue($window)
    $phase = $type.GetMethod('SetPhase', $flags)
    $picker.SelectedIndex = 1
    foreach ($name in @('hosts', 'prerequisite', 'download')) {
        [void]$phase.Invoke($window, [object[]]@($name))
    }
    if ($steps[2].Text -notmatch 'In progress' -or $status.Text -notmatch 'Downloading') {
        throw 'Missing-runtime download phase was not described.'
    }
    foreach ($name in @('publisher-check', 'microsoft-setup')) {
        [void]$phase.Invoke($window, [object[]]@($name))
    }
    if ($status.Text -notmatch "Microsoft's setup") { throw 'Microsoft setup phase was not described.' }
    [void]$phase.Invoke($window, [object[]]@('prerequisite-ready'))
    if ($steps[2].Text -notmatch 'Complete') { throw 'Microsoft setup was not marked complete.' }
    foreach ($name in @('verification', 'copy', 'previous-version', 'registration', 'menus', 'done')) {
        [void]$phase.Invoke($window, [object[]]@($name))
    }
    if ($steps[5].Text -notmatch 'Complete') { throw 'Previous-version replacement phase was not shown.' }
    'GRAPHICAL_INSTALLER_MISSING_RUNTIME=PASS DOWNLOAD_SIGNATURE_MICROSOFT_SETUP=SHOWN PREVIOUS_VERSION=REPLACED WITHOUT_NETWORK'
} finally {
    $window.Close()
    $window.Dispose()
}
$window = [Activator]::CreateInstance($type, $true)
try {
    $window.Show()
    $picker = $type.GetField('languagePicker', $flags).GetValue($window)
    $installButton = $type.GetField('install', $flags).GetValue($window)
    $closeButton = $type.GetField('close', $flags).GetValue($window)
    $status = $type.GetField('status', $flags).GetValue($window)
    $block = $type.GetMethod('BlockOnHosts', $flags)
    $cancel = $type.GetMethod('CancelHostWait', $flags)
    $names = [object[]]::new(1)
    $names[0] = [string[]]@('SAP2000 (PID 111)', 'CSiBridge (PID 222)')
    [void]$block.Invoke($window, $names)
    $picker.SelectedIndex = 1
    if ($installButton.Enabled -or -not $closeButton.Enabled -or $closeButton.Text -ne 'Cancel' -or
        $status.Text -notmatch 'SAP2000 \(PID 111\).*CSiBridge \(PID 222\)') {
        throw 'Open host sessions did not block installation and identify both programs.'
    }
    $picker.SelectedIndex = 0
    if ($status.Text -notmatch 'CHIUDERE LE SESSIONI' -or $closeButton.Text -ne 'Annulla') {
        throw 'The blocking request did not follow manual language selection.'
    }
    [void]$cancel.Invoke($window, [object[]]@())
    if (-not $installButton.Enabled -or $closeButton.Text -ne 'Chiudi') {
        throw 'Cancelling the wait did not restore the installer.'
    }
    'GRAPHICAL_INSTALLER_HOST_BLOCK=PASS NO_AUTOMATIC_CLOSE=PASS CANCELLATION=PASS'
} finally {
    $window.Close()
    $window.Dispose()
}
