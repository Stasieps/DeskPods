using System;
using System.Collections.Generic;

namespace PodsView;

internal static class Localization
{
    private static readonly Dictionary<string, Dictionary<string, string>> Strings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uk"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["close"]="Закрити", ["caseActivity"]="Кейс активний",
            ["about"]="ПРО ПРОГРАМУ",
            ["localPrivacy"]="Дані зберігаються на цьому ПК. Експорт діагностики не містить сирих пакетів і налаштувань.",
            ["supportDeveloper"]="🍺 Пивко розробнику ↗",
            ["supportNotConfigured"]="Посилання Ko-fi ще не налаштоване.",
            ["supportOpenError"]="Не вдалося відкрити браузер.",
            ["deviceAmbiguous"]="Поруч або в списку є кілька пар. Модель/колір не визначають унікальну пару.",
            ["modelFromSignal"]="Модель визначається з Bluetooth-пакета, а не назви пристрою.",

            ["hotkeyUnavailable"]="Гаряча клавіша недоступна. Зміни її в налаштуваннях.",
            ["connected"]="Підключено", ["waitingSignal"]="Очікую сигнал", ["searching"]="Пошук AirPods", ["disconnected"]="Не підключено", ["bluetoothUnavailable"]="Bluetooth недоступний", ["checkNeeded"]="Потрібна перевірка", ["starting"]="Запуск…",
            ["left"]="Лівий", ["right"]="Правий", ["case"]="Кейс", ["noData"]="Немає даних", ["openCase"]="Відкрий кейс", ["caseOpen"]="Кейс відкрито", ["closeCaseHint"]="Закрий кейс або натисни ×", ["charging"]="Заряджається", ["active"]="Активний", ["lastKnown"]="Останнє значення", 
            ["details"]="Детальніше", ["compact"]="Згорнути", ["refresh"]="Оновити", ["settings"]="Налаштування", ["designTheme"]="Тема дизайну", ["themeLabel"]="Оформлення", ["themeHint"]="Застосовується одразу.", ["copy"]="Копіювати", ["battery"]="Заряд", ["signal"]="Сигнал", ["balance"]="Баланс",
            ["autoUpdate"]="Оновлюється через Bluetooth", ["packetFreshness"]="Свіжість пакета", ["justNow"]="щойно", ["seconds"]="с", ["minutes"]="хв", ["hours"]="год", ["excellent"]="Відмінний", ["stable"]="Стабільний", ["weak"]="Слабкий",
            ["perfect"]="Рівно", ["watch"]="Зверни увагу", ["liveTelemetry"]="Дані наживо", ["packetsNormal"]="Пакети AirPods надходять нормально.", ["balanceStable"]="Різниця між навушниками: {0}%.", ["balanceWarning"]="Дисбаланс {0}% — перевір посадку в кейсі.", ["lowSoon"]="Один навушник скоро розрядиться.",
            ["waitingFresh"]="Очікую свіжі дані", ["wakeCase"]="Відкрий кейс на 2–3 секунди.", ["firstConnection"]="Потрібне перше підключення", ["pairInWindows"]="Додай AirPods у параметрах Bluetooth Windows.", ["notConnectedDetail"]="Підключи навушники або дозволь близьке сканування.", ["bluetoothDetail"]="Увімкни Bluetooth — сканування відновиться автоматично.", ["errorDetail"]="Натисни «Оновити» або відкрий журнал.",
            ["copied"]="Статус скопійовано", ["copiedDetail"]="Можна вставляти в повідомлення.", ["precision"]="AirPods передають заряд кроками по 10%", ["localOnly"]="Локально · без акаунта · без хмари", ["diagnostics"]="Журнал",
            ["settingsTitle"]="Налаштування", ["settingsSubtitle"]="Тільки потрібне. Без акаунта й хмари.", ["privacy"]="ПРИВАТНО", ["behavior"]="ПОВЕДІНКА", ["language"]="МОВА", ["languageLabel"]="Мова інтерфейсу", ["languageHint"]="Змінюється одразу після збереження.",
            ["startWindows"]="Запускати разом із Windows", ["startWindowsHint"]="DeskPods буде готовий до підключення навушників.", ["startTray"]="Стартувати тихо в треї", ["startTrayHint"]="Без великого вікна поверх робочого столу.", ["closeTray"]="Закриття ховає застосунок", ["closeTrayHint"]="Моніторинг продовжить працювати у фоні.",
            ["notifications"]="СПОВІЩЕННЯ", ["lowBattery"]="Низький заряд", ["lowBatteryHint"]="Одне корисне сповіщення, без спаму.", ["nearby"]="Бачити кейс до підключення", ["nearbyHint"]="Працює лише зі сильним сигналом уже спареної моделі.", ["cancel"]="Скасувати", ["save"]="Зберегти", ["openLogs"]="Відкрити журнал", ["collectLogs"]="Діагностика", ["collectLogsFail"]="Не вдалося зібрати діагностику. Деталі є в журналі.", ["exportLogs"]="Експорт логів (zip)", ["card"]="КАРТКА", ["hotkeyLabel"]="Гаряча клавіша картки", ["hotkeyHint"]="Натисни свою комбінацію. Esc — вимкнути. Показує останні відомі значення, навіть без нового пакета від кейса.", ["hotkeyOff"]="Вимкнено", ["showCard"]="Показати картку",
            ["openPodsView"]="Відкрити DeskPods", ["refreshScan"]="Оновити сканування", ["exit"]="Вийти", ["restartError"]="Не вдалося перезапустити Bluetooth. Деталі є в журналі.", ["startupError"]="Не вдалося змінити автозапуск.", ["backgroundTitle"]="DeskPods працює у фоні", ["backgroundBody"]="Відкрити можна через іконку біля годинника.", ["lowBatteryTitle"]="Низький заряд AirPods", ["hotkeyConflict"]="Ця комбінація недоступна. Попередню гарячу клавішу залишено.", ["saveSettingsError"]="Не вдалося зберегти налаштування на диск. Деталі в журналі.",
            ["lowBatteryHeader"]="Низький заряд", ["lowBatteryReason"]="{0}: {1} — низький заряд (поріг {2} %)", ["lowBatteryBody"]="{0} — {1}. Поріг сповіщення в налаштуваннях: {2} %. Час поставити на зарядку.", ["updateInstall"]="⬆ Оновити до v{0}", ["updateCheck"]="Перевірити оновлення", ["updateCheckFailed"]="Не вдалося перевірити оновлення. Перевір інтернет.", ["updateLatest"]="У тебе остання версія — v{0}.", ["updateTitle"]="Вийшла DeskPods v{0}", ["updateBody"]="Натисни, щоб завантажити й встановити. Це займе хвилину.", ["updateButton"]="Оновити до {0}", ["updateHint"]="Завантажити й встановити нову версію", ["updateDownloading"]="Завантаження {0} %…", ["updateFailed"]="Не вдалося встановити оновлення. Відкриваю сторінку релізу — можна завантажити вручну.", ["updates"]="ОНОВЛЕННЯ", ["updatesSetting"]="Перевіряти оновлення", ["updatesSettingHint"]="Раз на 6 годин питає GitHub про нову версію. Більше нічого не надсилається."
        },
        ["ru"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["close"]="Закрыть", ["caseActivity"]="Кейс активен",
            ["about"]="О ПРОГРАММЕ",
            ["localPrivacy"]="Данные хранятся на этом ПК. Экспорт диагностики не содержит сырых пакетов и настроек.",
            ["supportDeveloper"]="🍺 Пиво разработчику ↗",
            ["supportNotConfigured"]="Ссылка Ko-fi ещё не настроена.",
            ["supportOpenError"]="Не удалось открыть браузер.",
            ["deviceAmbiguous"]="Обнаружено несколько пар. Модель/цвет не определяют уникальную пару.",
            ["modelFromSignal"]="Модель определяется из Bluetooth-пакета, а не имени устройства.",

            ["hotkeyUnavailable"]="Горячая клавиша недоступна. Измени её в настройках.",
            ["connected"]="Подключено", ["waitingSignal"]="Жду сигнал", ["searching"]="Поиск AirPods", ["disconnected"]="Не подключено", ["bluetoothUnavailable"]="Bluetooth недоступен", ["checkNeeded"]="Нужна проверка", ["starting"]="Запуск…",
            ["left"]="Левый", ["right"]="Правый", ["case"]="Кейс", ["noData"]="Нет данных", ["openCase"]="Открой кейс", ["caseOpen"]="Кейс открыт", ["closeCaseHint"]="Закрой кейс или нажми ×", ["charging"]="Заряжается", ["active"]="Активен", ["lastKnown"]="Последнее значение", 
            ["details"]="Подробнее", ["compact"]="Свернуть", ["refresh"]="Обновить", ["settings"]="Настройки", ["designTheme"]="Тема дизайна", ["themeLabel"]="Оформление", ["themeHint"]="Применяется сразу.", ["copy"]="Копировать", ["battery"]="Заряд", ["signal"]="Сигнал", ["balance"]="Баланс",
            ["autoUpdate"]="Обновляется через Bluetooth", ["packetFreshness"]="Свежесть пакета", ["justNow"]="только что", ["seconds"]="с", ["minutes"]="мин", ["hours"]="ч", ["excellent"]="Отличный", ["stable"]="Стабильный", ["weak"]="Слабый",
            ["perfect"]="Ровно", ["watch"]="Обрати внимание", ["liveTelemetry"]="Данные в реальном времени", ["packetsNormal"]="Пакеты AirPods поступают нормально.", ["balanceStable"]="Разница между наушниками: {0}%.", ["balanceWarning"]="Дисбаланс {0}% — проверь посадку в кейсе.", ["lowSoon"]="Один наушник скоро разрядится.",
            ["waitingFresh"]="Жду свежие данные", ["wakeCase"]="Открой кейс на 2–3 секунды.", ["firstConnection"]="Нужно первое подключение", ["pairInWindows"]="Добавь AirPods в настройках Bluetooth Windows.", ["notConnectedDetail"]="Подключи наушники или разреши сканирование рядом.", ["bluetoothDetail"]="Включи Bluetooth — сканирование восстановится автоматически.", ["errorDetail"]="Нажми «Обновить» или открой журнал.",
            ["copied"]="Статус скопирован", ["copiedDetail"]="Можно вставлять в сообщение.", ["precision"]="AirPods передают заряд шагами по 10%", ["localOnly"]="Локально · без аккаунта · без облака", ["diagnostics"]="Журнал",
            ["settingsTitle"]="Настройки", ["settingsSubtitle"]="Только нужное. Без аккаунта и облака.", ["privacy"]="ПРИВАТНО", ["behavior"]="ПОВЕДЕНИЕ", ["language"]="ЯЗЫК", ["languageLabel"]="Язык интерфейса", ["languageHint"]="Изменится сразу после сохранения.",
            ["startWindows"]="Запускать вместе с Windows", ["startWindowsHint"]="DeskPods будет готов до подключения наушников.", ["startTray"]="Запускаться тихо в трее", ["startTrayHint"]="Без большого окна поверх рабочего стола.", ["closeTray"]="Закрытие прячет приложение", ["closeTrayHint"]="Мониторинг продолжит работать в фоне.",
            ["notifications"]="УВЕДОМЛЕНИЯ", ["lowBattery"]="Низкий заряд", ["lowBatteryHint"]="Одно полезное уведомление, без спама.", ["nearby"]="Видеть кейс до подключения", ["nearbyHint"]="Работает только с сильным сигналом уже сопряжённой модели.", ["cancel"]="Отмена", ["save"]="Сохранить", ["openLogs"]="Открыть журнал", ["collectLogs"]="Диагностика", ["collectLogsFail"]="Не удалось собрать диагностику. Детали в журнале.", ["exportLogs"]="Экспорт логов (zip)", ["card"]="КАРТОЧКА", ["hotkeyLabel"]="Горячая клавиша карточки", ["hotkeyHint"]="Нажми свою комбинацию. Esc — выключить. Показывает последние известные значения, даже без нового пакета от кейса.", ["hotkeyOff"]="Выключено", ["showCard"]="Показать карточку",
            ["openPodsView"]="Открыть DeskPods", ["refreshScan"]="Обновить сканирование", ["exit"]="Выйти", ["restartError"]="Не удалось перезапустить Bluetooth. Детали в журнале.", ["startupError"]="Не удалось изменить автозапуск.", ["backgroundTitle"]="DeskPods работает в фоне", ["backgroundBody"]="Открыть можно через значок возле часов.", ["lowBatteryTitle"]="Низкий заряд AirPods", ["hotkeyConflict"]="Эта комбинация недоступна. Предыдущая горячая клавиша сохранена.", ["saveSettingsError"]="Не удалось сохранить настройки на диск. Подробности в журнале.",
            ["lowBatteryHeader"]="Низкий заряд", ["lowBatteryReason"]="{0}: {1} — низкий заряд (порог {2} %)", ["lowBatteryBody"]="{0} — {1}. Порог уведомления в настройках: {2} %. Пора поставить на зарядку.", ["updateInstall"]="⬆ Обновить до v{0}", ["updateCheck"]="Проверить обновления", ["updateCheckFailed"]="Не удалось проверить обновления. Проверь интернет.", ["updateLatest"]="У тебя последняя версия — v{0}.", ["updateTitle"]="Вышла DeskPods v{0}", ["updateBody"]="Нажми, чтобы скачать и установить. Это займёт минуту.", ["updateButton"]="Обновить до {0}", ["updateHint"]="Скачать и установить новую версию", ["updateDownloading"]="Загрузка {0} %…", ["updateFailed"]="Не удалось установить обновление. Открываю страницу релиза — можно скачать вручную.", ["updates"]="ОБНОВЛЕНИЯ", ["updatesSetting"]="Проверять обновления", ["updatesSettingHint"]="Раз в 6 часов спрашивает GitHub о новой версии. Больше ничего не отправляется."
        },
        ["en"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["close"]="Close", ["caseActivity"]="Case activity",
            ["about"]="ABOUT",
            ["localPrivacy"]="Data stays on this PC. Diagnostic exports omit raw packets and personal settings.",
            ["supportDeveloper"]="🍺 Buy the developer a beer ↗",
            ["supportNotConfigured"]="The Ko-fi link has not been configured yet.",
            ["supportOpenError"]="The browser could not be opened.",
            ["deviceAmbiguous"]="Multiple pairs are present. Model/colour do not identify a unique pair.",
            ["modelFromSignal"]="Model identified from Bluetooth data, not the device name.",

            ["hotkeyUnavailable"]="The shortcut is unavailable. Choose another in Settings.",
            ["connected"]="Connected", ["waitingSignal"]="Waiting for signal", ["searching"]="Searching for AirPods", ["disconnected"]="Disconnected", ["bluetoothUnavailable"]="Bluetooth unavailable", ["checkNeeded"]="Needs attention", ["starting"]="Starting…",
            ["left"]="Left", ["right"]="Right", ["case"]="Case", ["noData"]="No data", ["openCase"]="Open the case", ["caseOpen"]="Case open", ["closeCaseHint"]="Close the case or press ×", ["charging"]="Charging", ["active"]="Active", ["lastKnown"]="Last known", 
            ["details"]="Details", ["compact"]="Compact", ["refresh"]="Refresh", ["settings"]="Settings", ["designTheme"]="Design theme", ["themeLabel"]="Appearance", ["themeHint"]="Applies instantly.", ["copy"]="Copy", ["battery"]="Battery", ["signal"]="Signal", ["balance"]="Balance",
            ["autoUpdate"]="Updates automatically over Bluetooth", ["packetFreshness"]="Packet age", ["justNow"]="just now", ["seconds"]="s", ["minutes"]="min", ["hours"]="h", ["excellent"]="Excellent", ["stable"]="Stable", ["weak"]="Weak",
            ["perfect"]="Even", ["watch"]="Needs attention", ["liveTelemetry"]="Live telemetry", ["packetsNormal"]="AirPods packets are arriving normally.", ["balanceStable"]="Difference between earbuds: {0}%.", ["balanceWarning"]="{0}% imbalance — check the fit in the case.", ["lowSoon"]="One earbud will need charging soon.",
            ["waitingFresh"]="Waiting for fresh data", ["wakeCase"]="Open the case for 2–3 seconds.", ["firstConnection"]="First connection required", ["pairInWindows"]="Add AirPods in Windows Bluetooth settings.", ["notConnectedDetail"]="Connect the earbuds or allow nearby scanning.", ["bluetoothDetail"]="Turn on Bluetooth — scanning will recover automatically.", ["errorDetail"]="Press Refresh or open the diagnostics log.",
            ["copied"]="Status copied", ["copiedDetail"]="Ready to paste into a message.", ["precision"]="AirPods broadcast battery in 10% steps", ["localOnly"]="Local · no account · no cloud", ["diagnostics"]="Diagnostics",
            ["settingsTitle"]="Settings", ["settingsSubtitle"]="Only what matters. No account or cloud.", ["privacy"]="PRIVATE", ["behavior"]="BEHAVIOR", ["language"]="LANGUAGE", ["languageLabel"]="Interface language", ["languageHint"]="Changes immediately after saving.",
            ["startWindows"]="Start with Windows", ["startWindowsHint"]="DeskPods will be ready before you connect the earbuds.", ["startTray"]="Start quietly in the tray", ["startTrayHint"]="No large window over your desktop.", ["closeTray"]="Close button hides the app", ["closeTrayHint"]="Monitoring continues in the background.",
            ["notifications"]="NOTIFICATIONS", ["lowBattery"]="Low battery", ["lowBatteryHint"]="One useful alert, no spam.", ["nearby"]="Show case before connection", ["nearbyHint"]="Uses only a strong signal from an already paired model.", ["cancel"]="Cancel", ["save"]="Save", ["openLogs"]="Open diagnostics", ["collectLogs"]="Diagnostics", ["collectLogsFail"]="Could not collect diagnostics. See the log for details.", ["exportLogs"]="Export logs (zip)", ["card"]="CARD", ["hotkeyLabel"]="Show-card shortcut", ["hotkeyHint"]="Press your combination. Esc switches it off. Shows the last known values even without a new case packet.", ["hotkeyOff"]="Off", ["showCard"]="Show the card",
            ["openPodsView"]="Open DeskPods", ["refreshScan"]="Refresh scanning", ["exit"]="Exit", ["restartError"]="Could not restart Bluetooth. See diagnostics for details.", ["startupError"]="Could not change startup settings.", ["backgroundTitle"]="DeskPods is running in the background", ["backgroundBody"]="Open it from the tray icon near the clock.", ["lowBatteryTitle"]="Low AirPods battery", ["hotkeyConflict"]="This shortcut is unavailable. The previous shortcut was kept.", ["saveSettingsError"]="Settings could not be saved to disk. See the log for details.",
            ["lowBatteryHeader"]="Low battery", ["lowBatteryReason"]="{0}: {1} — low battery (threshold {2} %)", ["lowBatteryBody"]="{0} is at {1}. Your alert threshold is {2} %. Time to charge it.", ["updateInstall"]="⬆ Update to v{0}", ["updateCheck"]="Check for updates", ["updateCheckFailed"]="Could not check for updates. Check your internet connection.", ["updateLatest"]="You are on the latest version — v{0}.", ["updateTitle"]="DeskPods v{0} is out", ["updateBody"]="Click to download and install. It takes about a minute.", ["updateButton"]="Update to {0}", ["updateHint"]="Download and install the new version", ["updateDownloading"]="Downloading {0} %…", ["updateFailed"]="The update could not be installed. Opening the release page so you can download it manually.", ["updates"]="UPDATES", ["updatesSetting"]="Check for updates", ["updatesSettingHint"]="Asks GitHub for a new version every 6 hours. Nothing else is sent."
        }
    };

    internal static string NormalizeLanguage(string? language) => AppSettings.NormalizeLanguage(language);

    internal static string T(string key, string? language = null)
    {
        string code = NormalizeLanguage(language ?? App.CurrentApp.Settings.Language);
        return Strings.TryGetValue(code, out Dictionary<string, string>? set) && set.TryGetValue(key, out string? value)
            ? value
            : Strings["uk"].TryGetValue(key, out value) ? value : key;
    }
}
