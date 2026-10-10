#!/usr/bin/env python3
"""Static/source-contract gate for 0.8.37 (not a C# compiler or WPF runtime test).

Every assertion here exists because something in this list once shipped broken:
a hardcoded version string, a white system dropdown, a colour written by hand in
a window instead of the palette, a handler removed from code but left in XAML.
0.8.29 deliberately changed fresh-resume and confirmed-wake-close handling.
Device tracker and packet filter remain byte-identical. 0.8.29 added familiar-device
listening epochs and a fresh same-counter reopen after >120s silence. Timers are unchanged.
The separately documented cache/parser/lifecycle fixes are covered by linked C# tests.
Passing this file never proves that the Windows build or animation executed.
"""
import hashlib
import os
import re
import sys
import xml.etree.ElementTree as ET

# The gate must check the tree it sits in. The old absolute path meant that the
# day the machine holding it went away, the script died on os.listdir instead of
# proving anything about the patch in hand.
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = os.path.join(ROOT, "src", "PodsView")

# The 0.8.17 backup folder this gate used to diff against is gone with that
# machine, and a check that cannot run proves nothing at all. These digests ARE
# the baseline now: the six files as they stand in 0.8.20, hashed exactly the way
# read() below returns them (UTF-8, newlines normalised by Python). Any edit to a
# lid, filter or parser file moves its digest and stops the patch here.
FROZEN_DIGESTS = {
    # 0.8.47: DeviceTracker.cs re-hashed after rewording one doc comment (comment lines
    # only). LidStateMachine.cs re-hashed because 0.8.47 changes its logic ON PURPOSE:
    # the case's first word since start/reset wakes (unknown silence counts as long), a
    # cycle that left the screen without a close is spent (no pop-back), late shut/open
    # copies are ignored, the lockout covers the same cycle only, the quiet tail is 6 s
    # and the close button marks its cycle spent. ParserSmoke, sim.py and the replay
    # fixtures pin that behaviour; this digest freezes the reviewed 0.8.47 text.
    "LidStateMachine.cs": "5304a327b9750415b76783eb7e818690fd0ea4b654385b30332b298bf92ad2ad",
    "DeviceTracker.cs": "32adc08d97bb2b0694b4e3bb30a85142cf59dd6309b545a5e0efb1fccb340c73",
    "PacketFilter.cs": "fd08e90ec66cce52a6886e90b1b50b112b66000275d6c217194b5a02d9fe3a1c",
    "BluetoothMonitor.cs": "4e94a573c728370c98e9317efcbb35a2b3e98458aa156a0e109b2bd1ec1f9676",
    "AirPodsAdvertisementParser.cs": "43e45cfc6cfcdf24a2b2f41d9bc999bddce51eff3bf622ce5c5c6cb41015cd78",
    "LidSignal.cs": "36c06b8f4ebf9ba1b3eceb5e04b564068af3de106a65ad002474274c1463b04d",
}

WINDOWS = ["MainWindow.xaml", "CasePopupWindow.xaml", "SettingsWindow.xaml"]
# These reviewed digests are the approved 0.8.37 source contract. They are the lid state
# machine, the filter and the parser: the parts sim.py covers. LidSignal.cs left this
# list in 0.8.20 because that patch changes it on purpose; it is checked by rule below.
FROZEN = [
    "LidStateMachine.cs",
    "DeviceTracker.cs",
    "PacketFilter.cs",
]

failures = []
checks = 0


def read(*parts):
    with open(os.path.join(*parts), encoding="utf-8") as handle:
        return handle.read()


def check(condition, message):
    global checks
    checks += 1
    if not condition:
        failures.append(message)


sources = {name: read(SRC, name) for name in os.listdir(SRC) if name.endswith((".cs", ".xaml", ".csproj"))}
app_xaml = sources["App.xaml"]


def markup(name):
    """XAML with its comments removed.

    A verifier that reads comments accuses the note explaining a removal instead of
    the removal itself. That is the same mistake as matching a field declaration and
    calling it the method body.
    """
    return re.sub(r"<!--.*?-->", "", sources[name], flags=re.S)


def csharp(name):
    """C# with its comments removed.

    Named csharp(), not code(): a local variable called code already exists further
    down and shadowed the helper, which is why the first run of this gate crashed
    instead of checking anything.

    Same reason as markup(): a rule about behaviour must be checked against code.
    Reading prose made the gate accuse a doc comment that explained a removal - and
    the other way round, a comment could satisfy a rule the code never followed.
    """
    return re.sub(r"//.*", "", re.sub(r"/\*.*?\*/", "", sources[name], flags=re.S))

# ---------------------------------------------------------------- version
# 0.8.42: the version is no longer a literal in this gate. VERSION in the repo root is
# the single source of truth and .github/scripts/deskpods_version.py copies it into the
# csproj, START.cmd, the installer and the README. A literal here is what made this gate,
# and with it every release attempt, fail from 0.8.38 onwards.
repo_version = read(ROOT, "VERSION").strip()
check(re.fullmatch(r"\d+\.\d+\.\d+", repo_version) is not None, "VERSION does not hold an X.Y.Z number")
check(f"<Version>{repo_version}</Version>" in sources["PodsView.csproj"], "csproj does not carry the version from VERSION")
check('Assembly.GetName().Version?.ToString(3)' in sources['App.xaml.cs'], 'App version must come from assembly metadata')
check("0.8.24" not in sources["PodsView.csproj"], "csproj still mentions 0.8.24")
# The number the user reads must come from App.Version, never from a literal.
for name in WINDOWS:
    check(not re.search(r'v0\.\d+\.\d+', markup(name)), f"{name} carries a hardcoded version string")
check('VersionLabel.Text = "v" + App.Version' in sources["MainWindow.xaml.cs"], "main window does not read App.Version")
# 0.8.22: the settings header lost its subtitle and its version line, so that window
# no longer prints a build number at all. The pill on the card is the only place it
# is written, and the check above proves that one reads the property.
for gone in ["VersionText", "SubtitleText", "settingsSubtitle"]:
    check(gone not in sources["SettingsWindow.xaml"] and gone not in sources["SettingsWindow.xaml.cs"],
          f"the removed settings header line {gone} is still here")

# ---------------------------------------------------------------- palette discipline
for name in WINDOWS:
    literals = re.findall(r'"#[0-9A-Fa-f]{3,8}"', sources[name])
    check(not literals, f"{name} still writes colours by hand: {literals}")
check('Background="{DynamicResource PopupCanvasBrush}"' in sources["CasePopupWindow.xaml"], "popup card is not on the palette")
check('BorderBrush="{DynamicResource PopupRimBrush}"' in sources["CasePopupWindow.xaml"], "popup rim is not on the palette")
check('OpacityMask="{StaticResource PopupDotFadeMask}"' in sources["CasePopupWindow.xaml"], "popup dot mask is not shared")
check('OpacityMask="{StaticResource DotFadeMask}"' in sources["MainWindow.xaml"], "main window dot mask is not shared")
check('x:Name="PopupLiveGlow"' in sources["CasePopupWindow.xaml"], "the popup glow cannot be recoloured")
check('PopupLiveGlow.Color = glow.Color' in sources["CasePopupWindow.xaml.cs"], "the popup glow is never repainted")

# ---------------------------------------------------------------- typography
for token in ["DisplayFont", "MonoFont", "UiFont", "FontNano", "FontMicro", "FontBody", "FontTitle", "FontValue", "FontHeadline", "FontHero"]:
    check(f'x:Key="{token}"' in app_xaml, f"typography token {token} is missing")
for name in WINDOWS:
    numeric = re.findall(r'FontSize="[0-9.]+"', sources[name])
    check(not numeric, f"{name} still sets font sizes by hand: {numeric}")
    families = re.findall(r'FontFamily="(?!\{)[^"]+"', sources[name])
    check(not families, f"{name} still names a font family by hand: {families}")
for name, text in sources.items():
    check("Segoe MDL2 Assets" not in text, f"{name} still depends on the Segoe MDL2 icon font")

# ---------------------------------------------------------------- fonts that exist
# 0.8.18 put "Cascadia Mono" and "Segoe UI Variable Text" in the tokens. Both are
# Windows 11 extras, so on this machine WPF walked the fallback list, landed on the
# system default, and the entire typography patch was invisible: "the fonts are not
# changed". Naming a font is a guess; asking the machine is a measurement. The probe
# rewrites the tokens at startup, which only works while every consumer binds
# dynamically, so that is a rule now and not a habit.
for name in list(WINDOWS) + ["App.xaml"]:
    frozen = re.findall(r"\{StaticResource (DisplayFont|UiFont|MonoFont)\}", sources[name])
    check(not frozen, f"{name} binds a font token statically, so the probe cannot replace it: {frozen}")
check("internal static class FontProbe" in sources["Infrastructure.cs"], "there is no font probe")
check("Fonts.SystemFontFamilies" in sources["Infrastructure.cs"], "the probe does not read the installed fonts")
check("Fonts resolved" in sources["Infrastructure.cs"], "the probe does not log which family won")
check("FontProbe.Apply(Resources)" in sources["App.xaml.cs"], "the font probe never runs at startup")
check("FontProbe.Report" in sources["Infrastructure.cs"], "the export does not carry the resolved fonts")
for token in ["DisplayFont", "UiFont", "MonoFont"]:
    check(re.search(rf'"{token}"(?:, "[^"]+")+', sources["Infrastructure.cs"]) is not None,
          f"the probe has no candidate list for {token}")
# The last candidate in every list must be a font that ships with every supported
# Windows, otherwise the probe can still end with nothing to draw.
check('"Arial" }' in sources["Infrastructure.cs"], "the display and ui lists have no universal last resort")
check('"Courier New" }' in sources["Infrastructure.cs"], "the mono list has no universal last resort")

# ---------------------------------------------------------------- pixel grid
# "Not tidy" is measurable: half-pixel edges and half-pixel glyph stems.
main_window = sources["MainWindow.xaml"]
check('UseLayoutRounding="True"' in main_window, "the main window does not snap to whole pixels")
check('TextOptions.TextFormattingMode="Display"' in main_window, "small text is not fitted to the pixel grid")
check('Style="{StaticResource TitleText}"' in main_window, "the device name does not use the display face")
check(main_window.count('Style="{StaticResource ChipText}"') == 3, "the L / R / C chips are not one shared style")
check(main_window.count('<RowDefinition Height="44" />') == 3, "the three battery rows are not one height")
check(main_window.count('<ColumnDefinition Width="84" />') == 3, "the value columns are not one width")

# ---------------------------------------------------------------- constructor order
# Settings stopped opening because WPF raises SelectionChanged while the constructor
# is still filling the combo boxes, and the handler read a field that was assigned
# eleven lines further down: null.Length, swallowed by the dispatcher, no window. A
# field a handler can reach must not start as null, and a handler must be able to
# tell that the window is not built yet.
for name in ["MainWindow.xaml.cs", "SettingsWindow.xaml.cs", "CasePopupWindow.xaml.cs"]:
    bare = re.findall(r"private string (\w+);", sources[name])
    check(not bare, f"{name} leaves a string field null for its own handlers: {bare}")
settings_code = sources["SettingsWindow.xaml.cs"]
check("private bool _ready;" in settings_code, "settings has no constructor-finished flag")
check(settings_code.count("if (!_ready) return;") >= 2, "the selection handlers do not wait for the constructor")
check(settings_code.count("_ready = true;") == 1, "the constructor-finished flag is not set exactly once")
check(settings_code.split("_ready = true;")[0].rstrip().endswith("RefreshEnabledStates();"),
      "the flag is not the last statement of the constructor")

# ---------------------------------------------------------------- the dropdown
check('x:Name="PART_Popup"' in app_xaml, "the ComboBox dropdown is still the system one")
check('TargetType="ComboBoxItem"' in app_xaml, "dropdown rows are not styled")
check('TargetType="ScrollBar"' in app_xaml, "the dropdown scrollbar is not styled")
check('TargetType="TextBox"' in app_xaml, "the hotkey field would be a white box")
check('x:Name="SelectionSite"' in app_xaml, "the closed dropdown has no content presenter")
check(app_xaml.count("IsHighlighted") >= 1 and app_xaml.count("IsSelected") >= 1, "dropdown rows have no hover or selected state")
# Every brush the dropdown paints with has to be a palette brush, or a theme can
# leave it white again.
combo = app_xaml[app_xaml.index('TargetType="ComboBox"'):]
for brush in ["RaisedBrush", "ElevatedBrush", "BorderBrush", "PrimaryTextBrush", "SecondaryTextBrush"]:
    check(brush in combo, f"the dropdown template does not use {brush}")

# ---------------------------------------------------------------- themes
theme = sources["ThemeManager.cs"]
check("internal static event Action? Changed" in theme, "themes cannot notify open windows")
check("Changed?.Invoke()" in theme, "the theme event is never raised")
for field in ["PopupCanvas", "PopupRim", "PopupGlow"]:
    check(f'Set(resources, "{field}"' in theme, f"Apply() does not publish {field}")
# 0.8.47: five looks (TERMINAL, CARBON and NEON were removed), each with its own popup
# card: one default on the field plus four overrides - mono keeps the field value.
check(theme.count("PopupCanvas = ") == 5, f"expected 1 default plus 4 popup overrides, found {theme.count('PopupCanvas = ')}")
check(theme.count("PopupRim = ") == 5, f"expected 1 default plus 4 rim overrides, found {theme.count('PopupRim = ')}")
check(theme.count("PopupGlow = ") == 5, f"expected 1 default plus 4 glow overrides, found {theme.count('PopupGlow = ')}")
check(theme.count("new Palette") == 5, f"expected 5 palettes, found {theme.count('new Palette')}")
palette_ids = re.findall(r'new Palette\s*\{\s*Id = "([a-z]+)"', theme)
setting_ids = re.findall(r'"([a-z]+)"', re.search(r'ThemeIds = \{([^}]*)\}', sources["AppSettings.cs"]).group(1))
check(palette_ids == setting_ids, f"theme ids differ: palettes {palette_ids}, settings {setting_ids}")
for gone in ["terminal", "carbon", "neon"]:
    check(gone not in palette_ids and gone not in setting_ids, f"the removed theme {gone} is still offered")
check("eink" in palette_ids and "4  E-INK  (paper)" in theme, "the E-INK theme is missing or misnumbered")
check("value.Length == 8" in theme, "the palette parser cannot read the popup opacity")
check('resources["ProductGlow"] = BuildGlow' in theme, "the halo behind the photo is not theme aware")

# ---------------------------------------------------------------- one window, one job
main_xaml = sources["MainWindow.xaml"]
main_cs = sources["MainWindow.xaml.cs"]
main_markup = markup("MainWindow.xaml")
main_code = re.sub(r"//[^\n]*|/\*.*?\*/", "", main_cs, flags=re.S)
for gone in ["DetailRoot", "LIVE TELEMETRY", "BALANCE", "SIGNAL", "DetailsButton", "Details_Click", "CopyStatus_Click", "Compact_Click"]:
    check(gone not in main_markup, f"MainWindow.xaml still holds {gone}")
    check(gone not in main_code, f"MainWindow.xaml.cs still holds {gone}")
check("Assets/airpods-turn-still.png" in main_window, "Derived approved-photo still is not wired; source hash is checked below")
check(os.path.exists(os.path.join(SRC, "Assets", "airpods-case-open.png")), "the case photo is missing from Assets")
check("ThemeManager.Changed += RefreshVisuals" in main_cs, "the tile does not repaint on a theme switch")
check("ThemeManager.Changed -= RefreshVisuals" in main_cs, "the tile never unsubscribes")

# ---------------------------------------------------------------- settings
set_xaml = sources["SettingsWindow.xaml"]
set_cs = sources["SettingsWindow.xaml.cs"]
check(set_xaml.count('Click="Export_Click"') == 1, "there must be exactly one export button")
for gone in ["OpenLogs_Click", "CollectLogs_Click", "OpenLogsButtonText", "CollectLogsButtonText"]:
    check(gone not in set_xaml and gone not in set_cs, f"the split log buttons survive as {gone}")
check("Task.Run(Logger.CollectDiagnostics)" in set_cs, "the export button does not build the zip")
check("BatteryFormat" not in set_xaml, "the removed battery format card is still in the settings window")
check('x:Name="HotkeyBox"' in set_xaml, "no shortcut control")
check('PreviewKeyDown="Hotkey_PreviewKeyDown"' in set_xaml, "the shortcut field records nothing")
check("10, 15, 20, 25, 30, 35, 40, 45, 50" in set_cs, "the low battery threshold is not in steps of five")

# ---------------------------------------------------------------- settings model
infra = sources["Infrastructure.cs"] + sources["AppSettings.cs"] + sources["BatteryFormat.cs"]
for member in ["ShowCardHotkey"]:
    check(f"public string {member} {{ get; set; }}" in infra, f"AppSettings has no {member}")
    check("MemberwiseClone()" in infra, "Settings snapshot does not copy all scalar fields")
    check(f"value.{member} =" in infra, f"Load() does not normalize {member}")
check("internal static class BatteryFormat" in infra, "BatteryFormat is missing")
# 0.8.19 required the range format here (step + "-" + (step + 9)). 0.8.20 removed
# ranges on purpose, so that line contradicted the rule twenty checks below and was
# a failure nobody ever saw, because this script died before it could print.
check('internal static string Percent' in infra, "there is no single place that formats a percentage")
check("internal sealed class HotkeyService" in infra, "HotkeyService is missing")
check("RegisterHotKey" in infra and "UnregisterHotKey" in infra, "the shortcut is not registered with Windows")
check("0x0312" in infra, "WM_HOTKEY is not handled")
check("ModNoRepeat" in infra, "holding the keys would fire a burst of cards")
check('DefaultHotkey = "Ctrl+Alt+P"' in infra and 'Default = AppSettings.DefaultHotkey' in infra, 'Default shortcut sources disagree')

# ---------------------------------------------------------------- show on demand
app_cs = sources["App.xaml.cs"]
check("internal void ShowCardManually()" in app_cs, "the card cannot be shown on demand")
check(app_cs.count("_hotkeys.Rebind") + app_cs.count("_hotkeys?.Rebind") == 2, "the shortcut is not bound at start and after saving")
check('Localization.T("showCard")' in app_cs, "the tray menu cannot show the card")
check("_lastData = _batteryMemory.Data" in sources["App.xaml.cs"], "Accepted telemetry is not remembered independently of the window")
check("_hotkeys?.Dispose();" in app_cs, "the shortcut is not released on exit")
check('Logger.Trace("show reason=manual")' in app_cs, "a manual card leaves no trace line")
check('Logger.Trace("hide reason=manual-timeout")' in app_cs, "the manual card hide leaves no trace line")
# The manual timer must never fight a real open cycle.
check("if (_lid.IsOpen) return;" in app_cs, "the manual timer could hide a live popup")
# The lid machine may be read here, never driven.
manual = app_cs[app_cs.index("internal void ShowCardManually()"):app_cs.index("internal void ShowMainWindow()")]
for forbidden in ["_lid.Force", "_lid.Apply", "_lid.Observe", "_lid.Reset"]:
    check(forbidden not in manual, f"the manual card drives the lid machine through {forbidden}")
check(app_cs.count("RebuildTrayMenu();\n        RebuildTrayMenu();") == 0, "the tray menu is still rebuilt twice in a row")

# ---------------------------------------------------------------- popup honesty
popup_cs = sources["CasePopupWindow.xaml.cs"]
check("internal void ShowManual(" in popup_cs, "the popup cannot be shown by hand")
check("_caseFreshForCurrentCycle = false;" in popup_cs.split("internal void ShowManual(")[1], "a hand-opened card would claim the case is live")
check("_dismissedForCurrentOpenCycle" not in popup_cs.split("internal void ShowManual(")[1].split("internal void HideForClosedCase")[0], "ShowManual touches the dismissed flag of a real cycle")
check("BatteryFormat.Percent(battery)" in popup_cs, "the popup does not use the shared battery format")
check("ThemeManager.Changed += OnThemeChanged" in popup_cs, "the popup does not repaint on a theme switch")
check("ThemeManager.Changed -= OnThemeChanged" in popup_cs, "the popup never unsubscribes")

# ---------------------------------------------------------------- wiring
# Every handler named in XAML must exist in its code-behind, or the build dies.
for name in WINDOWS:
    code = sources[name.replace(".xaml", ".xaml.cs")]
    for handler in sorted(set(re.findall(r'(?:Click|SelectionChanged|Checked|Unchecked|KeyDown|PreviewKeyDown|MouseLeftButtonDown)="([A-Za-z_]+)"', sources[name]))):
        check(f"void {handler}(" in code, f"{name} calls {handler}, which does not exist")

# Every resource key a window asks for must exist in App.xaml or be published by ThemeManager.
defined = set(re.findall(r'x:Key="([^"]+)"', app_xaml))
for entry in re.findall(r'Set\(resources, "([A-Za-z]+)"', theme):
    defined.add(entry + "Color")
    defined.add(entry + "Brush")
defined.update({"DotPattern", "ProductGlow"})
for name in WINDOWS + ["App.xaml"]:
    for key in sorted(set(re.findall(r'\{(?:Static|Dynamic)Resource ([A-Za-z0-9_]+)\}', sources[name]))):
        check(key in defined, f"{name} asks for the missing resource {key}")

# Every control the code-behind touches must carry that name in the XAML.
for name in WINDOWS:
    code = sources[name.replace(".xaml", ".xaml.cs")]
    names = set(re.findall(r'x:Name="([^"]+)"', sources[name]))
    for used in sorted(set(re.findall(r'\b((?:Popup|Compact|Version|Title|Subtitle|Privacy|Language|Theme|Card|Battery|Hotkey|Behavior|Notifications|Nearby|Threshold|Start|Minimize|Export|Cancel|Save|Case|Shell)[A-Za-z0-9]*(?:Text|Box|Button|Label|Root|Fill|Scale|Dot|Glow|Hint))\b', code))):
        if used in {"ComboBox", "TextBox", "CheckBox", "MessageBox", "BatteryOption", "ThemeOption", "LanguageOption"}:
            continue
        check(used in names, f"{name.replace('.xaml', '.xaml.cs')} uses {used}, which no element is named")

# ---------------------------------------------------------------- frozen logic
for name in FROZEN:
    new = hashlib.sha256(read(SRC, name).encode("utf-8")).hexdigest()
    check(new == FROZEN_DIGESTS[name], f"{name} differs from the reviewed 0.8.37 reset/timing/binding contract")

# ------------------------------------------------- the one frozen file that moved, and why
# 0.8.20 changes LidSignal on purpose. A learned signature used to keep the time of its
# first lesson for ever, so twelve hours into a session it went cold even though the case
# had been leaning on it all along - and the first word of the next address was discarded.
# That is why it did not pop on the first try, only on the second. A file allowed
# to move must still be held to the rule that moved it, so the gate checks the rule.
lid_signal = sources["LidSignal.cs"]
# Intentional R3 fix: strict per-address expiry and dated shape persistence.
check("now - previous.At <= Forget" in lid_signal, "Address TTL is not enforced at lookup")
check("ExportState()" in lid_signal and 'v2|' in lid_signal, "Shape persistence loses its age")
check("private struct ShapeMemory" in lid_signal, "a signature has no separate learned and used time")
check("public DateTimeOffset UsedAt;" in lid_signal, "a signature does not remember when it was last used")
check("private bool ShapeIsFresh" in lid_signal, "freshness is not measured by its own rule")
check("now - memory.UsedAt <= ShapeTtl" in lid_signal, "freshness is still measured from the first lesson")
check("TimeSpan.FromDays(7)" in lid_signal, "the signature lifetime is still the twelve hours that expired mid-session")
check(lid_signal.count("LearnedAt") >= 4, "the saved order no longer rests on the learned time")
check("pair.Value.UsedAt > oldestAt" in lid_signal, "a full table drops the oldest signature instead of the idlest")
check("else if (knownShape)" in lid_signal, "a proven signature still cannot rescue a repeated byte")
for decision in ["explicit", "fresh", "known", "baseline", "change", "hold", "static"]:
    check(f'source = "{decision}"' in lid_signal, f"the lid decision no longer reports {decision}")
model = read(ROOT, "tests", "portable", "sim.py")
check("def _fresh(" in model, "the model does not measure freshness the way the code does")
check("self._shapes[value] = (entry[0], now)" in model, "the model does not refresh a signature it uses")
check("must not go cold" in model, "the model has no regression for the first opening after a pause")
check("SHAPE_TTL = 7 * 24 * 3600.0" in model, "the model and the code disagree on the signature lifetime")

# ---------------------------------------------------------------- battery text
# The radio sends one nibble per element. 0.8.20 prints the middle of the step it stands
# for, which is the same number MagicPods shows and never more than 5 percent out.
infra = sources["Infrastructure.cs"] + sources["AppSettings.cs"] + sources["BatteryFormat.cs"]
settings_code = sources["SettingsWindow.xaml.cs"]
check("internal static int Midpoint" in infra, "the battery text has no midpoint")
check("value is 0 or 100" in infra, "Endpoint readings are changed")
check("Math.Min(100, value + 5)" in infra, "Midpoint conversion changed")
check("internal static int Value(int value) => Midpoint(" in infra, "the printed number is not unconditionally the midpoint of the step")
check("Normalize(string? mode)" not in infra, "the battery mode normaliser outlived the setting it served")
infra_code = csharp("Infrastructure.cs")
check('"range"' not in infra_code, "the range mode is still alive in the settings code")
check('+ "\u2013" +' not in infra_code, "the battery text still builds a range with a dash")
check("BatteryFormat.Bar(" in sources["MainWindow.xaml.cs"], "the main window bar can still disagree with its number")
check("BatteryFormat.Bar(" in sources["CasePopupWindow.xaml.cs"], "the popup bar can still disagree with its number")
# 0.8.22: the dropdown is gone on request, so everything that fed it has to be gone
# too - otherwise a dead mode rots in the settings file and in this gate.
for gone in ["BatteryDisplay", "BatteryOption", "BatteryFormatComboBox", "BatteryFormatLabelText", "BatteryFormatHintText"]:
    check(gone not in infra and gone not in settings_code and gone not in set_xaml,
          f"the removed battery format setting left {gone} behind")
for name in ["SettingsWindow.xaml.cs", "Localization.cs", "Infrastructure.cs"]:
    check("80\u201389" not in csharp(name), f"{name} still shows the 80-89 % range")
check("batteryExact" not in sources["Localization.cs"], "a label for a mode that no longer exists")
check("batteryRange" not in sources["Localization.cs"], "a label for the range that was removed")
check("MagicPods" not in sources["Localization.cs"], "the hint for a control that no longer exists is still translated")

# ---------------------------------------------------------------- launchers
# ---------------------------------------------------------------- 0.8.22 card
# There is no compiler in reach while this patch is written, so the gate carries
# the checks a build would have made: every XAML file must at least be well-formed.
for name in WINDOWS + ["App.xaml"]:
    try:
        ET.fromstring(sources[name])
        check(True, "")
    except ET.ParseError as error:
        check(False, f"{name} is not well-formed XML: {error}")

probe = sources["Infrastructure.cs"] + sources["BatteryFormat.cs"]
main_code = sources["MainWindow.xaml.cs"]
localization = sources["Localization.cs"]

# Type: a designed medium weight for the name, Corbel for text, Consolas for the
# digits. All three ship with Windows, so the probe cannot land on "nothing
# changed" the way 0.8.18 did.
check('"DisplayFont", "Segoe UI"' in probe, "the device name is not on the chosen UI face")
check('"UiFont", "Segoe UI"' in probe, "the ui face is not Segoe UI")
check('"MonoFont", "Consolas"' in probe, "the digits are not on Consolas first")
check('<FontFamily x:Key="DisplayFont">Segoe UI,' in app_xaml, "the XAML chain for the name starts elsewhere")
check('<FontFamily x:Key="UiFont">Segoe UI,' in app_xaml, "the XAML chain for text starts elsewhere")
check('<FontFamily x:Key="MonoFont">Consolas,' in app_xaml, "the XAML chain for the digits starts elsewhere")
# Bahnschrift is the obvious DIN-style choice and is deliberately absent: it is a
# variable font, WPF resolves one static instance of it, and a weight request then
# returns a face nobody picked. The two Windows-11-only faces never existed here.
probe_code = csharp("Infrastructure.cs")
app_markup = markup("App.xaml")
for absent in ["Bahnschrift", "Cascadia Mono", "Segoe UI Variable"]:
    # Comment-blind on purpose: why a face is not here belongs in a comment, and a
    # gate that reads its own explanation as a violation is the 0.8.21 bug again.
    check(absent not in probe_code and absent not in app_markup,
          f"{absent} is back in the font chain")

# The name sat indented over a status line that starts at the edge: WPF centres a
# Stretch-aligned TextBlock whose MaxWidth is smaller than its slot. A layout
# defect that read as a font defect.
title = re.search(r'<Style x:Key="TitleText".*?</Style>', app_xaml, re.S)
check(title is not None, "the title style is unreadable")
title_style = title.group(0) if title else ""
check('Property="HorizontalAlignment" Value="Left"' in title_style, "the device name can drift back into the middle of the header")
check('Value="Bold"' not in title_style, "the name asks for a bold face the display font does not carry")
check("FontHeadline" in title_style, "the device name is not at headline size")
check('Value="Ideal"' in title_style, "the big name is back on the pixel-snapped Display mode")
check('Property="LineHeight"' in title_style, "the headline line box is unbounded again")
check(main_window.count('<RowDefinition Height="54" />') == 1, "the header row cannot hold a 22 px name")

# The status line lies on the dot field, so it needs size, weight and the primary
# colour. Same lesson as the version pill: small grey text over a texture is not
# text.
status = re.search(r'<Style x:Key="StatusLabel".*?</Style>', app_xaml, re.S)
check(status is not None, "the status style is unreadable")
status_style = status.group(0) if status else ""
check("FontMicro" in status_style, "the status line is not at the requested small size")
check("PrimaryTextBrush" in status_style, "the status line is grey on a texture again")
check('Property="FontWeight"' in status_style, "the status line has no weight of its own")
check('Style="{StaticResource StatusLabel}"' in main_window, "the status line does not use that style")

# Removed on request in 0.8.22: the captions over the bars and the freshness line
# in the corner, with everything that fed them. A gate that only asserts presence
# lets removals rot, so these are the other half of the same rule.
for gone in ["RowCaption", "CompactLeftCaption", "CompactRightCaption", "CompactCaseCaption",
             "FooterText", "FreshnessText", "UpdateCaptions", "UpdateFooter", "CaseIsStale",
             "CaseFresh", "PacketFresh", "Spaced(", "ToCharArray()", "_lastPacketAt", "_caseAt",
             "bool stale"]:
    check(gone not in main_window and gone not in main_code and gone not in app_xaml,
          f"{gone} was removed on request and is still here")
# 0.8.23: the refresh button is gone. It restarted a scan the passive listener was
# already running, and no amount of rescanning makes a closed case broadcast. The
# keyboard path stays, so the removal must be of the button only.
for gone in ["CompactRefreshButton", "Refresh_Click", 'ToolTip="Refresh (R)"']:
    check(gone not in main_window and gone not in main_code,
          f"{gone} was removed on request and is still here")
check("RestartScan()" in main_code, "the manual rescan path is gone entirely")
check("Key.R" in main_code, "the R shortcut left with the button")
check(main_window.count('Click="Settings_Click"') == 1 and main_window.count('Click="ShellClose_Click"') == 1,
      "the header must keep exactly the settings and close buttons")

# ---- the case row: a remembered number instead of a dash
# The case reports its own charge only in the packets it sends with the lid open;
# with the buds out and the lid shut that nibble is 15, "no value", and the row drew
# a dash for hours. The last broadcast value is persisted and restored - and it must
# not be allowed to look live, or the card starts inventing telemetry.
check("LastCaseBattery" in infra, "the last case reading has nowhere to live")
check("MemberwiseClone()" in infra, "Snapshot drops system-managed state")
check("Settings.RememberCase(" in sources["App.xaml.cs"], "Case state must be persisted independently of the window")
check("Settings.Save();" in sources["App.xaml.cs"], "Remembered state never reaches disk")
check("_caseIsLastKnown" in main_code, "the card cannot tell a remembered value from a live one")
check("lastKnown ? Token(\"SecondaryTextBrush\")" in main_code,
      "a remembered case value is painted like a live one")
check("nibble is >= 0 and <= 10 ? nibble * 10 : null" in csharp("AirPodsAdvertisementParser.cs"),
      "the parser started inventing a case value the radio did not send")

# ---------------------------------------------------------------- photo turntable
import json
manifest = json.loads(read(SRC, "Assets", "airpods-turn.json"))
photo = open(os.path.join(SRC, "Assets", "airpods-case-open.png"), "rb").read()
check(hashlib.sha256(photo).hexdigest() == manifest["sourceSha256"], "Animation source is not the approved photo")
check(manifest["notA360Model"] is True, "Do not mislabel a photo depth effect as a recovered 360 model")
player = csharp("ProductTurntablePlayer.cs")
check('x:Name="ProductImage"' in main_window, "Product image is not connected to the window")
check("new ProductTurntablePlayer(this, ProductImage)" in main_code, "Product playback is not wired")
check("_image.Source = _frames[index]" in player, "Frame updates never reach the displayed image")
check("_timer.Tick += Tick" in player and "_timer.Start()" in player, "Playback timer is not wired")
check("_owner.IsVisible" in player and "WindowState.Minimized" in player, "Hidden/minimized playback is not stopped")
check("SystemParameters.ClientAreaAnimation" in player, "Reduced-motion preference is ignored")
check("_timer.Stop()" in player and "StaticPropertyChanged -= " in player, "Playback resources are not released")
check("ProductSheen" not in main_window and "Viewport3D" not in main_window, "Retired mismatched mask/3D renderer remains")
for asset in ["airpods-turn-atlas.png", "airpods-turn-still.png"]:
    check(os.path.isfile(os.path.join(SRC, "Assets", asset)), "Missing animation asset " + asset)
    check(asset in sources["PodsView.csproj"], "Animation asset is not packaged: " + asset)
check("Version =" not in main_code, "Window hardcodes its own version")
check("ApplyPreferences(updated)" in sources["App.xaml.cs"] and "Settings = updated" not in sources["App.xaml.cs"], "Preferences overwrite live state")
check("Bound: true, Trusted: true" in sources["App.xaml.cs"], "UI coalescer accepts rejected packets")
check("if (!packet.Bound || !packet.Trusted) return false" in csharp("BatteryMemory.cs"), "State accepts rejected packets")
check("LeftAt" in csharp("BatteryMemory.cs") and "RightAt" in csharp("BatteryMemory.cs"), "Earbud timestamps are not independent")
check("notify: false" in csharp("BluetoothMonitor.cs"), "Per-packet status bypasses coalescing")
check("_refreshLock.Dispose()" not in csharp("BluetoothMonitor.cs"), "Semaphore may be disposed while an operation owns it")
check("_restartLock.WaitAsync" in csharp("BluetoothMonitor.cs"), "Restart operations are not serialized")
check("_lidSignal.Reset()" in csharp("BluetoothMonitor.cs"), "Radio restart retains transient signal trust")
check("lock (SaveGate)" in sources["AppSettings.cs"] and "Guid.NewGuid()" in sources["AppSettings.cs"], "Settings writes are not isolated")
check("_written" in infra and "lock (FileGate)" in infra, "Logging has no writer completion/snapshot barrier")
check("BatteryFormat.ShouldAlert" in csharp("App.xaml.cs"), "Notification threshold ignores display units")
for n, text in sources.items():
    check("\ufffd" not in text, n + " contains replacement characters")

for key in ["updated", "dataAge", "ago"]:
    check(f'["{key}"]=' not in localization, f"the dead key {key} is still translated")

# The pill keeps its opaque fill: reading a build number through a texture is the
# problem that started 0.8.21.
check('x:Key="VersionChip"' in app_xaml, "there is no version pill style")
chip = re.search(r'<Style x:Key="VersionChip".*?</Style>', app_xaml, re.S)
check(chip is not None, "the version pill style is unreadable")
chip_text = chip.group(0) if chip else ""
check('Property="Background"' in chip_text, "the version pill has no fill, so the dots run through the digits")
check("Transparent" not in chip_text, "the version pill fill is transparent")
check(re.search(r'<Border (?:x:Name="VersionChip" )?Grid\.Row="2" Style="\{StaticResource VersionChip\}"[^>]*>\s*<TextBlock x:Name="VersionLabel"', main_window) is not None,
      "the build number is not inside the pill")
check('x:Key="VersionChipText"' in app_xaml, "the pill has no text style")

# The bars of the approved card, and the two hairlines between the three rows.
check(main_window.count('Height="4" CornerRadius="2"') == 3, "the three bars are not the 4 px track of the approved card")
check('{StaticResource TrackBorder}" Height="6"' not in main_window, "an old 6 px track is still in the card")
check(main_window.count('Grid.ColumnSpan="4" Height="1"') == 2, "the row hairlines are not exactly two")

# Space: with the captions and the corner line gone the card was mostly air below
# the third row. 0.8.23 keeps the same 244 px shell and spends six of the footer
# pixels on the bigger name: 244 = 1 + 16 + 54 header + 132 rows + 24 footer + 16 + 1.
size = re.search(r'Width="540" Height="(\d+)" MinWidth="540" MinHeight="(\d+)"', main_window)
check(size is not None, "the card no longer states its size")
check(bool(size) and size.group(1) == "244" and size.group(1) == size.group(2), "the card is not the 244 px shell")
check('<RowDefinition Height="48" />' not in main_window, "the header row is back at the old height")
check(main_window.count('<RowDefinition Height="132" />') == 1, "the body row moved")
check(main_window.count('<RowDefinition Height="44" />') == 3, "the three value rows moved")
check('Value(value) + " %"' in probe, "the percent sign is glued to the digits again")
check("text.Length > 5" in main_code, "the value column steps down at the old length")

# The build script is part of the product: if it refuses to start, the patch
# does not exist for the user.
cmd_names = sorted(n for n in os.listdir(ROOT) if n.endswith(".cmd"))
check(cmd_names == ["START.cmd"], "Root must contain exactly one START.cmd, not duplicate launchers")
for name in cmd_names:
    with open(os.path.join(ROOT, name), "rb") as handle:
        raw = handle.read()
    # A BOM becomes part of the first command name: cmd looks for "\ufeff@echo".
    check(not raw.startswith(b"\xef\xbb\xbf"), f"{name} starts with a UTF-8 BOM")
    check(all(byte < 0x80 for byte in raw), f"{name} holds non-ASCII bytes")
    check(b"\r\n" in raw, f"{name} has no CRLF line endings")
    check(raw.startswith(b"@echo off"), f"{name} does not open with @echo off")

launcher = read(ROOT, "START.cmd")
csproj_version = re.search(r"<Version>([^<]+)</Version>", sources["PodsView.csproj"]).group(1)
check(f'set "EXPECTED={csproj_version}"' in launcher, "the launcher's EXPECTED does not match the project version")
# A guard must probe the property, never a literal of the previous release:
# that is what turned a correct 0.8.18 tree into "THIS FOLDER IS STILL OLD".
for stale in sorted(set(re.findall(r"0\.\d+\.\d+", launcher))):
    check(stale == csproj_version, f"the launcher hardcodes version {stale}")
build = read(ROOT, "scripts", "build-run.ps1")
deploy = read(ROOT, "scripts", "safe-deploy.ps1")
check("$xml.Project.PropertyGroup.Version" in build, "Launcher does not inspect the project version")
check("PodsView.ParserSmoke" in build and "PodsView.UiSmoke" in build, "Launcher bypasses core or WPF tests")
check(build.index("PodsView.UiSmoke") < build.index("Start-PodsViewCandidate -Exe"), "Old process can be stopped before WPF tests")
check("taskkill" not in launcher and "goto :launch" not in launcher, "Unsafe batch launch fallback remains")
check("Copy-Item" in deploy and "Source and installation folders overlap" in deploy, "Install overlap guard missing")
check("Get-ChildItem -Path $installDir" not in deploy, "Installer deletes the working installation")
check("Undo-PodsViewDeployment" in deploy, "No rollback path")


# r3 packaging regressions: repaired source + clean, explicit test execution.
smoke = read(ROOT, "tests", "PodsView.ParserSmoke", "Program.cs")
check("Five echoes spanning 800 ms prove a sustained burst" not in smoke, "Obsolete fifth-echo assertion shipped")
# 0.8.42: the marker must exist and name a revision, but not a frozen release number.
check(re.search(r'parser tests: \d+\.\d+\.\d+ / [a-z0-9-]+', smoke) is not None, "Repaired test revision missing")
check("fifthEcho == LidAction.None" in smoke and "sixthEcho == LidAction.Open" in smoke, "Two-stage echo proof regression missing")
check("799" in smoke and "AddMilliseconds(800)" in smoke, "Echo boundary regression missing")
check(build.count("'--no-incremental'") == 2, "Both test projects need forced rebuilds")
check("Invoke-DotnetChecked -Arguments @($coreDll) -ExpectedMarker" in build, "Core launch is not pinned to a checked fresh output")
check("Invoke-DotnetChecked -Arguments @($uiDll,$qa)" in build, "WPF launch is not pinned to a fresh output")
check(".build\\test-runs\\" in build, "Test outputs are not isolated per attempt")
check("@('run','--project'" not in build, "Implicit cached test launch remains")
check(os.path.isfile(os.path.join(ROOT, "START.cmd")), "Distinct r3 launcher missing")

# r4: executable references must agree; never silence NETSDK1151 instead.
ui_project_text = read(ROOT, "tests", "PodsView.UiSmoke", "PodsView.UiSmoke.csproj")
# 0.8.42: release.yml (a ZIP on every push to main) was replaced by the tag-driven
# build-windows.yml, which also builds the installer, plus ci.yml for every branch.
# These rules are about the build mode, so they follow the workflows that now build.
workflow_text = read(ROOT, ".github", "workflows", "build-windows.yml") + read(ROOT, ".github", "workflows", "ci.yml")
check("<SelfContained>true</SelfContained>" in ui_project_text and "<SelfContained>true</SelfContained>" in sources["PodsView.csproj"], "Executable projects disagree on self-contained mode")
check('AdditionalProperties="SelfContained=false' not in ui_project_text, "Conflicting reference override remains")
check("'build',$uiProject,'-c','Release','-r',$runtime,'--self-contained','true'" in build, "WPF tests do not build for the candidate runtime")
check("-p:SelfContained=false" not in build and "-p:SelfContained=false" not in workflow_text, "A launcher still forces conflicting executable modes")
check("'build',$uiProject,'-c','Release','--no-incremental','-p:PublishSingleFile=false'" not in build, "WPF launch overrides the application's publishing configuration")
check("ValidateExecutableReferencesMatchSelfContained>false" not in ui_project_text + sources["PodsView.csproj"] and "ValidateExecutableReferencesMatchSelfContained=false" not in build + workflow_text, "SDK compatibility validation was disabled")
check("[Console]::OutputEncoding = $utf8" in build and "$OutputEncoding = $utf8" in build, "Native console encoding does not match PowerShell")
check("DOTNET_CLI_UI_LANGUAGE = 'en-US'" in build and "chcp 65001" in launcher, "Readable console output is not configured")
check("@($uiDll,$qa) -ExpectedMarker 'DeskPods tray r4: approved logo and WPF resources verified'" in build, "WPF completion is not checked")
check("-r win-x64 --self-contained true --no-incremental" in workflow_text, "CI does not match the repaired WPF build mode")

# 0.8.37 presentation changes: retain Classic, reject accidental UI/radio coupling.
appearance = sources["MainWindow.Appearance.cs"]
tray_appearance = sources["TrayAppearance.cs"]
settings = sources["AppSettings.cs"]
check('Id = "refined"' in theme and 'Id = "mono"' in theme, "Refined or Classic palette lost")
check('Classic - MONO+' in theme, "Classic cannot be identified in the menu")
check('public string Theme { get; set; } = "refined"' in settings, "New default is not Refined")
check('value.PreviousTheme = value.Theme' in settings and 'value.SettingsRevision < 9' in settings, "Previous palette is not preserved during migration")
check('new CornerRadius(12)' in appearance and 'new RectangleGeometry' in appearance, "Rounded content clipping missing")
check('_restoreAppearance.Clear()' in appearance and 'target.ClearValue(property)' in appearance, "Classic overrides cannot be restored")
check('_productPlayer.SetEnabled(!refined)' in appearance, "Refined runs the rejected animation")
check('airpods-case-open.png' in sources['ProductTurntablePlayer.cs'] and 'if (!_enabled)' in player, "Refined still-photo path missing")
check('ThemeManager' not in tray_appearance and 'TrayIconFactory.Create' not in tray_appearance, "Case tray must not depend on the window theme or use the old renderer")
check('"deskpods-tray-dp13-dark"' in tray_appearance and '"deskpods-tray-dp13-light"' in tray_appearance and 'SystemUsesLightTheme' in tray_appearance, "Tray dp13 glyph must follow the taskbar tone")
check('battery' not in tray_appearance.split('Create(')[1].split('ReadLightTaskbar')[0].replace('int? battery',''), "Battery state must not replace the tray glyph")
check('GetManifestResourceStream' in tray_appearance and 'DeskPods.Brand.Icon' in sources['PodsView.csproj'] and 'DeskPods.Tray.Light' in sources['PodsView.csproj'] and 'DeskPods.Tray.Dark' in sources['PodsView.csproj'], "Tray icons are not embedded")
check('_trayAppearanceKey == appearanceKey' in app_cs, "Theme switch cannot invalidate the tray icon cache")
for tone in ['light', 'dark']:
    for state in ['normal', 'disconnected', 'low', 'warning']:
        check(os.path.isfile(os.path.join(SRC, 'Assets', 'Tray', f'case-{tone}-{state}.ico')), f'Missing tray icon {tone}/{state}')
check('RefinedForVerification' in read(ROOT,'tests','PodsView.UiSmoke','Program.cs'), "Windows Refined/Classic regression checks missing")
check('Classic choice did not survive restart' in read(ROOT,'tests','PodsView.ParserSmoke','CoreRegressions.cs'), "Classic persistence check missing")

# 0.8.37: narrow latency changes; preserve fresh/trust/binding and manual-dismiss guards.
startup_buffer = read(SRC, "StartupObservationBuffer.cs")
monitor28 = read(SRC, "BluetoothMonitor.cs")
lid28 = read(SRC, "LidStateMachine.cs")
latency_tests = read(ROOT, "tests", "PodsView.ParserSmoke", "PopupLatencyRegressions.cs")
check("PopupLatencyRegressions.Run(Expect);" in smoke, "Popup latency tests are not executed by the core runner")
check("StartupObservationBuffer.cs" in read(ROOT,"tests","PodsView.ParserSmoke","PodsView.ParserSmoke.csproj"), "Actual startup buffer source is not linked into core tests")
check("PrimePairingCatalog(devices);" in monitor28, "Startup pairing catalog is not primed")
check(monitor28.index("PrimePairingCatalog(devices);") < monitor28.index("BluetoothDevice.FromIdAsync"), "Slow connection queries still block startup catalog")
check("if (!_pairingCatalogReady && !StartupEligibility.CanProcess" in monitor28 and "_startupObservations.Add(observation)" in monitor28, "Early startup observations are still dropped")
check("ProcessObservation(observation)" in monitor28 and "observation.RadioAgeMs + waited" in monitor28, "Buffered observations bypass guards or gain freshness")
check("_latest[observation.Address] = observation" in startup_buffer, "Startup buffer replays superseded lid states")
check("Capacity = 24" in startup_buffer and "TimeSpan.FromSeconds(2)" in startup_buffer, "Startup buffer lost its capacity/age limits")
# 0.8.47: no resume door any more - a spent cycle stays down; a fresh new opening shows at once.
check("if (provenFresh && (!echoOfClosedCycle || idleReopen))" in lid28, "Fresh openings still wait for another packet")
check("if (spent)" in lid28 and "MarkSpent(_liveCycle, _liveAddress, now, byHand: true)" in lid28 and "MarkSpent(_liveCycle, _liveAddress, now, byHand: false)" in lid28, "a cycle that left the screen can pop back")
check('LastBlockReason = "late shut word, cycle "' in lid28 and 'LastBlockReason = "late open word, cycle "' in lid28, "late copies can flicker the card")
check("TimeSpan sinceCase = heardBefore ? now - _lastCaseAt : TimeSpan.MaxValue;" in lid28, "the first word after start, boot or unlock cannot wake the card")
check("_wakeUnconfirmed && cycle == _wakeCycle && _wakeOpenedAt != default" in lid28, "Wake floor still blocks changed-cycle closes")
check("_wakeUnconfirmed = false;\n            _wakeOpenedAt = default;\n            _wakeCycle = -1;" in lid28, "Open confirmation does not clear the provisional floor")
check("neighbour manufactured startup silence" in latency_tests and "Fresh resume change bypassed manual dismissal" in latency_tests, "Latency changes lack negative regressions")

check("observation.Previous?.ReceivedAt ?? now" in monitor28 and "_lidSignal.Seed(_readCaseShapes(key), observedAt)" in monitor28, "First observed packet loses saved signature memory")
check("previous with { Previous = null }" in startup_buffer and "Fresh(previous)" in startup_buffer, "Startup transition proof is unbounded or stale")
check("Startup coalescing erased a nonexplicit lid transition" in latency_tests, "Nonexplicit first-opening regression missing")
# 0.8.46: an earbud resting in a shut case keeps its last live charge, in grey.
mem = sources["BatteryMemory.cs"]
check("LeftBattery = data.LeftBattery ?? LastLeft" in mem and "RightBattery = data.RightBattery ?? LastRight" in mem, "a silent earbud falls back to a dash instead of its last charge")
check("LeftCached = data.LeftCached || data.LeftBattery is null" in mem, "a remembered earbud value is not marked as last known")
check("RememberEarbuds(_batteryMemory.LastLeft, _batteryMemory.LastRight" in sources["App.xaml.cs"], "earbud charges are not persisted")
check("LastLeftBattery" in sources["AppSettings.cs"] and "LastRightBattery" in sources["AppSettings.cs"], "settings do not store the earbud charges")
check("saved.LastRightBattery" in sources["MainWindow.xaml.cs"], "the window does not restore the earbud charges at start")
# 0.8.46: a low channel blinks and the alert says what and why.
check("LowBatteryPulse.Set(low" in sources["MainWindow.xaml.cs"], "the low row in the window does not blink")
check("LowBatteryPulse.Set(low" in sources["CasePopupWindow.xaml.cs"], "the low channel on the card does not blink")
check('"lowBatteryHeader"' in sources["CasePopupWindow.xaml.cs"] and "_alertKey" in sources["CasePopupWindow.xaml.cs"], "the low-battery card does not name its reason")
# 0.8.47: the low-battery alert is a balloon only - it no longer pops the card by itself.
check('ShowCardManually(low.Key)' not in sources["App.xaml.cs"] and '"lowBatteryBody"' in sources["App.xaml.cs"], "the low-battery alert pops the card by itself or does not explain itself")
for key in ["lowBatteryHeader", "lowBatteryReason", "lowBatteryBody", "updateInstall", "updateCheck", "updateTitle", "updateBody", "updateButton", "updateFailed", "updatesSetting", "updatesSettingHint", "updateDownloading", "updateLatest", "updateCheckFailed", "updateHint"]:
    check(sources["Localization.cs"].count('["' + key + '"]') == 3, "localization key missing in a language: " + key)
# 0.8.46: updates from GitHub Releases, one click, switchable.
upd = sources.get("UpdateService.cs", "") + sources.get("UpdateRelease.cs", "")
check("api.github.com/repos/" in upd and "Stasieps/DeskPods" in upd, "the updater does not ask GitHub Releases")
check('"prerelease"' in upd and '"draft"' in upd, "the updater would offer drafts or pre-releases")
check("DeskPods_Setup_v{latest.ToString(3)}.exe" in upd, "the updater does not pick the versioned setup")
check("UpdateRelease.cs" in read(ROOT, "tests", "PodsView.ParserSmoke", "PodsView.ParserSmoke.csproj") and "UpdateRelease.Pick(" in read(ROOT, "tests", "PodsView.ParserSmoke", "CoreRegressions.cs"), "the release choice is not covered by the parser smoke tests")
check("TrustedDownload" in upd and "Uri.UriSchemeHttps" in upd, "the updater accepts non-GitHub or non-HTTPS downloads")
check("'M'" in upd and "'Z'" in upd, "the updater runs a download without checking it is an executable")
check("/SILENT" in upd and "IsInstalledCopy" in upd, "the updater does not install silently or replaces portable copies")
# 0.8.47: the setup must hash to the SHA-256 GitHub publishes with the asset.
check('"digest"' in upd and "SHA256.HashData" in upd and "UpdateRelease.DigestAccepts(info.InstallerSha256, hash)" in upd, "the updater runs a setup without checking its published SHA-256")
check("UpdateRelease.DigestAccepts(" in read(ROOT, "tests", "PodsView.ParserSmoke", "CoreRegressions.cs") and "UpdateRelease.Sha256Hex(" in read(ROOT, "tests", "PodsView.ParserSmoke", "CoreRegressions.cs"), "the SHA-256 check is not covered by the parser smoke tests")
check("Check: WizardSilent" in read(ROOT, "installer", "DeskPods.iss"), "a silent update does not restart DeskPods")
check("CheckForUpdates" in sources["AppSettings.cs"] and "UpdatesCheckBox" in sources["SettingsWindow.xaml"], "update checks cannot be switched off")
check("if (Settings.CheckForUpdates) CheckForUpdates(manual: false)" in sources["App.xaml.cs"], "automatic checks ignore the setting")
check("VersionChip_MouseLeftButtonDown" in sources["MainWindow.xaml"] and "App.CurrentApp.InstallUpdate()" in sources["MainWindow.xaml.cs"], "the window has no update button")
# ---------------------------------------------------------------- 0.8.47
# The in-case door, its diagnostics, and the lid log that comes with every export.
classifier = sources["CaseSignalClassifier.cs"]
check('source = "in-case"' in classifier and "data.ModelCode == 0x1420" in classifier and "provenFresh && rememberedPair" in classifier, "the in-case door is missing or lost a guard")
check(classifier.index("if (OutsideCase(data))") < classifier.index('source = "in-case"'), "an earbud outside the case can reach the in-case door")
for word in ['"in-case"', '"spent"', '"late"']:
    check(word in sources["DiagnosticSanitizer.cs"], f"the safe export drops the {word} decision word")
infra = sources["Infrastructure.cs"]
check("podsview-trace-lid.log" in infra and "internal static bool IsLidLine(string text)" in infra, "the lid log is missing")
check("if (_lid.IsOpen)" in sources["App.xaml.cs"], "closing a card shown by hand buries a lid cycle")
# sim.py mirrors the 0.8.47 timings of LidStateMachine.
for sim_text, code_text in [("QUIET_TAIL = 6.0", "QuietTail = TimeSpan.FromSeconds(6)"),
                            ("BURST_WINDOW = 20.0", "BurstWindow = TimeSpan.FromSeconds(20)"),
                            ("SPENT_WINDOW = 120.0", "SpentWindow = TimeSpan.FromSeconds(120)"),
                            ("WAKE_SILENCE = 10.0", "WakeSilence = TimeSpan.FromSeconds(10)"),
                            ("WAKE_QUIET = 6.0", "WakeQuiet = TimeSpan.FromSeconds(6)"),
                            ("TIMEOUT = 10.0", "StreamTimeout = TimeSpan.FromSeconds(10)"),
                            ("LOCKOUT = 0.25", "_closeLockout = TimeSpan.FromMilliseconds(250)")]:
    check(sim_text in model and code_text in lid28, f"sim.py and LidStateMachine disagree: {sim_text} / {code_text}")

print(f"check18: {checks} checks, {len(failures)} failures")
for failure in failures:
    print("  FAIL " + failure)
sys.exit(1 if failures else 0)
