using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace ByteBridge.Localization;

/*
 * Simple localization manager for Arabic and English.
 *
 * Loads strings from a dictionary based on the current
 * culture. Falls back to English if a translation is missing.
 */
public static class Strings
{
    private static readonly Dictionary<string, Dictionary<string, string>> Translations = new()
    {
        ["en"] = new()
        {
            // Window
            ["AppTitle"] = "ByteBridge",
            ["AddData"] = "+ Add Data",
            ["Done"] = "Done",
            ["Settings"] = "Settings",

            // Gateway
            ["GatewayApi"] = "Gateway API",
            ["Port"] = "Port",
            ["CopyApiKey"] = "Copy API Key",
            ["NewKey"] = "New Key",
            ["StartService"] = "Start Service",
            ["TurnOn"] = "Turn On",
            ["TurnOff"] = "Turn Off",
            ["Answering"] = "● Answering — {0}",
            ["TurnedOff"] = "● Turned off",
            ["Starting"] = "● Starting, or unable to bind",
            ["NotRunning"] = "● Not running",
            ["ServiceRunning"] = "Service: running",
            ["ServiceStopped"] = "Service: stopped",
            ["ServicePending"] = "Service: starting or stopping",
            ["ServiceNotInstalled"] = "Service: not installed — reinstall ByteBridge to add it",
            ["TunnelHint"] = "Point the tunnel here:  cloudflared tunnel --url {0}",
            ["TurnedOffHint"] = "The gateway is set not to listen. A tunnel pointed at this machine will return 502 until it is turned on.",
            ["StartingHint"] = "The service is running but nothing answers on {0}. Give it a few seconds; if it stays this way the port is in use or the reservation was refused. See Event Viewer, Application, source ByteBridge.",
            ["NotRunningHint"] = "The service that hosts the gateway is not running, so a tunnel pointed at this machine will return 502.",

            // Connections
            ["NoDatabases"] = "No databases configured.\n\nClick + Add Data to create a connection.",
            ["Online"] = "● Online",
            ["Offline"] = "● Offline",
            ["OfflineButton"] = "Offline",
            ["OnlineButton"] = "Online",
            ["Edit"] = "Edit",
            ["Delete"] = "Delete",

            // Cloudflare OAuth
            ["CloudflareLogin"] = "Cloudflare Login",
            ["NotConfigured"] = "Not configured",
            ["Enabled"] = "Enabled",
            ["TeamDomain"] = "Team Domain",
            ["Audience"] = "Audience",
            ["Enable"] = "Enable",
            ["Disable"] = "Disable",

            // Dialogs
            ["CopyKeyTitle"] = "API Key",
            ["CopyKeyMessage"] = "API key copied.\n\nSend it on every request as the X-API-Key header.",
            ["CopyKeyError"] = "The key could not be copied.\n\n{0}",
            ["NewKeyTitle"] = "New API Key",
            ["NewKeyConfirm"] = "Generate a new API key?\n\nEvery client still using the current key will be rejected until it is updated.",
            ["NewKeyMessage"] = "A new API key was generated.\n\nUse Copy API Key to put it on the clipboard.",
            ["DeleteTitle"] = "Delete Database",
            ["DeleteConfirm"] = "Delete \"{0}\"?\n\nThis connection will be permanently removed.",
            ["TurnOnlineTitle"] = "Turn Online",
            ["TurnOnlineConfirm"] = "Turn \"{0}\" online?\n\nByteBridge will test the database connection first.",
            ["TurnOfflineTitle"] = "Turn Offline",
            ["TurnOfflineConfirm"] = "Turn \"{0}\" offline?",
            ["ConnectionFailed"] = "Connection Failed",
            ["ConnectionFailedMessage"] = "The database connection failed.\n\nThe connection will remain Offline.",
            ["ConnectionFailedError"] = "The database connection failed.\n\n{0}",
            ["InvalidPort"] = "Please enter a valid port between 1 and 65535.",
            ["ServiceError"] = "The ByteBridge service could not be started.\n\n{0}",
            ["ServiceTitle"] = "Service",

            // OAuth dialogs
            ["OAuthDisabled"] = "Cloudflare OAuth login has been disabled.\n\nUsers will need to use the API key to authenticate.",
            ["OAuthEnabled"] = "Cloudflare OAuth login has been enabled.\n\nMake sure you have configured Cloudflare Access\nwith an identity provider and created an application\nfor your gateway hostname.",
            ["OAuthTeamDomainRequired"] = "Please enter your Cloudflare Access team domain.\n\nExample: my-team.cloudflareaccess.com",
            ["OAuthAudienceRequired"] = "Please enter the Access application audience tag.\n\nFind it in Zero Trust → Access → Applications → Settings.",

            // Close dialog
            ["CloseTitle"] = "ByteBridge",
            ["CloseMessage"] = "What would you like to do?",
            ["MinimizeToTray"] = "Minimize to Tray",
            ["ExitApp"] = "Exit",
            ["Cancel"] = "Cancel",

            // Settings
            ["AutoStart"] = "Start with Windows",
            ["AutoStartHint"] = "ByteBridge will start automatically when you log in.",
            ["Language"] = "Language",
            ["English"] = "English",
            ["Arabic"] = "العربية",
        },

        ["ar"] = new()
        {
            // Window
            ["AppTitle"] = "ByteBridge",
            ["AddData"] = "+ إضافة بيانات",
            ["Done"] = "تم",
            ["Settings"] = "الإعدادات",

            // Gateway
            ["GatewayApi"] = "واجهة API",
            ["Port"] = "المنفذ",
            ["CopyApiKey"] = "نسخ مفتاح API",
            ["NewKey"] = "مفتاح جديد",
            ["StartService"] = "بدء الخدمة",
            ["TurnOn"] = "تشغيل",
            ["TurnOff"] = "إيقاف",
            ["Answering"] = "● يعمل — {0}",
            ["TurnedOff"] = "● متوقف",
            ["Starting"] = "● قيد التشغيل، أو تعذر الاتصال",
            ["NotRunning"] = "● غير يعمل",
            ["ServiceRunning"] = "الخدمة: تعمل",
            ["ServiceStopped"] = "الخدمة: متوقفة",
            ["ServicePending"] = "الخدمة: قيد التشغيل أو الإيقاف",
            ["ServiceNotInstalled"] = "الخدمة: غير مثبتة — أعد تثبيت ByteBridge لإضافتها",
            ["TunnelHint"] = "وجّه النفق هنا:  cloudflared tunnel --url {0}",
            ["TurnedOffHint"] = "تم إعداد البوابة لعدم الاستماع. سيُرجع النفق الموجّه إلى هذا الجهاز خطأ 502 حتى يتم تشغيله.",
            ["StartingHint"] = "الخدمة تعمل ولكن لا أحد يستجيب على {0}. انتظر بضع ثوانٍ؛ إذا استمر هذا، فالمنفذ مستخدم أو تم رفض الحجز. راجع عارض الأحداث، التطبيق، مصدر ByteBridge.",
            ["NotRunningHint"] = "الخدمة التي تستضيف البوابة غير تعمل، لذا سيُرجع النفق الموجّه إلى هذا الجهاز خطأ 502.",

            // Connections
            ["NoDatabases"] = "لا توجد قواعد بيانات مُعدّة.\n\nانقر على + إضافة بيانات لإنشاء اتصال.",
            ["Online"] = "● متصل",
            ["Offline"] = "● غير متصل",
            ["OfflineButton"] = "غير متصل",
            ["OnlineButton"] = "متصل",
            ["Edit"] = "تعديل",
            ["Delete"] = "حذف",

            // Cloudflare OAuth
            ["CloudflareLogin"] = "تسجيل الدخول عبر Cloudflare",
            ["NotConfigured"] = "غير مُعدّ",
            ["Enabled"] = "مُفعّل",
            ["TeamDomain"] = "نطاق الفريق",
            ["Audience"] = "الجمهور",
            ["Enable"] = "تفعيل",
            ["Disable"] = "تعطيل",

            // Dialogs
            ["CopyKeyTitle"] = "مفتاح API",
            ["CopyKeyMessage"] = "تم نسخ مفتاح API.\n\nأرسله في كل طلب كـ X-API-Key header.",
            ["CopyKeyError"] = "تعذر نسخ المفتاح.\n\n{0}",
            ["NewKeyTitle"] = "مفتاح API جديد",
            ["NewKeyConfirm"] = "توليد مفتاح API جديد؟\n\nسيتم رفض كل عميل لا يزال يستخدم المفتاح الحالي حتى يتم تحديثه.",
            ["NewKeyMessage"] = "تم توليد مفتاح API جديد.\n\nاستخدم نسخ مفتاح API لوضعه على الحافظة.",
            ["DeleteTitle"] = "حذف قاعدة البيانات",
            ["DeleteConfirm"] = "حذف \"{0}\"؟\n\nسيتم إزالة هذا الاتصال نهائياً.",
            ["TurnOnlineTitle"] = "تشغيل الاتصال",
            ["TurnOnlineConfirm"] = "تشغيل \"{0}\"؟\n\nسيقوم ByteBridge باختبار اتصال قاعدة البيانات أولاً.",
            ["TurnOfflineTitle"] = "إيقاف الاتصال",
            ["TurnOfflineConfirm"] = "إيقاف \"{0}\"؟",
            ["ConnectionFailed"] = "فشل الاتصال",
            ["ConnectionFailedMessage"] = "فشل اتصال قاعدة البيانات.\n\nسيبقى الاتصال غير متصل.",
            ["ConnectionFailedError"] = "فشل اتصال قاعدة البيانات.\n\n{0}",
            ["InvalidPort"] = "الرجاء إدخال منفذ صالح بين 1 و 65535.",
            ["ServiceError"] = "تعذر بدء خدمة ByteBridge.\n\n{0}",
            ["ServiceTitle"] = "الخدمة",

            // OAuth dialogs
            ["OAuthDisabled"] = "تم تعطيل تسجيل الدخول عبر Cloudflare OAuth.\n\nسيحتاج المستخدمون إلى استخدام مفتاح API للمصادقة.",
            ["OAuthEnabled"] = "تم تفعيل تسجيل الدخول عبر Cloudflare OAuth.\n\nتأكد من إعداد Cloudflare Access\nمزوّد هوية وإنشاء تطبيق\nلنطاق اسم بوابتك.",
            ["OAuthTeamDomainRequired"] = "الرجاء إدخال نطاق فريق Cloudflare Access.\n\nمثال: my-team.cloudflareaccess.com",
            ["OAuthAudienceRequired"] = "الرجاء إدخال علامة جمهور تطبيق Access.\n\nاعثر عليها في Zero Trust → Access → Applications → Settings.",

            // Close dialog
            ["CloseTitle"] = "ByteBridge",
            ["CloseMessage"] = "ماذا تريد أن تفعل؟",
            ["MinimizeToTray"] = "تصغير إلى صينية النظام",
            ["ExitApp"] = "خروج",
            ["Cancel"] = "إلغاء",

            // Settings
            ["AutoStart"] = "البدء مع Windows",
            ["AutoStartHint"] = "سيبدأ ByteBridge تلقائياً عند تسجيل الدخول.",
            ["Language"] = "اللغة",
            ["English"] = "English",
            ["Arabic"] = "العربية",
        }
    };

    private static string _currentLanguage = "en";

    public static string CurrentLanguage => _currentLanguage;

    public static void SetLanguage(string language)
    {
        _currentLanguage = language;

        var culture = language switch
        {
            "ar" => new CultureInfo("ar"),
            _ => new CultureInfo("en")
        };

        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    public static string Get(string key)
    {
        if (Translations.TryGetValue(
                _currentLanguage,
                out var strings) &&
            strings.TryGetValue(key, out var value))
        {
            return value;
        }

        // Fallback to English
        if (Translations.TryGetValue(
                "en",
                out var english) &&
            english.TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        return key;
    }

    public static string Format(string key, params object[] args)
    {
        var template = Get(key);
        return string.Format(template, args);
    }
}
