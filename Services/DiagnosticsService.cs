using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MPWS.Models;

namespace MPWS.Services;

public static class DiagnosticsService
{
    const string Ok = "Норма", Risk = "Риск фризов", Info = "Справочно", Unknown = "Не проверено";
    internal const string Graphics = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    static string? Value(string path, string name) =>
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + path, name, null)?.ToString();
    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    internal static async Task<string> Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, file))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (file=="bcdedit.exe") {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            start.StandardOutputEncoding=Encoding.GetEncoding(System.Globalization.CultureInfo.InstalledUICulture.TextInfo.OEMCodePage);
        }
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var p = Process.Start(start) ?? throw new IOException("Процесс не запустился.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { p.Kill(true); throw new IOException("Истекло время проверки."); }
        var result = await stdout;
        await stderr;
        if (p.ExitCode != 0) throw new IOException("Windows не предоставила результат (код " + p.ExitCode + "). " + "Проверьте доступ к системному компоненту.");
        return result;
    }
    static Task<string> PS(string script) => Run(@"WindowsPowerShell\v1.0\powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand",
        Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';" + script)));

    public static async Task<List<Finding>> Scan(IProgress<string> progress)
    {
        List<Finding> list = [];
        async Task Group(string name, Func<Task> read)
        {
            progress.Report(name);
            try { await read(); }
            catch (Exception ex) { list.Add(new(name, Unknown, ex.Message, "Проверка недоступна на этой системе.")); }
        }
        await Group("Параметры загрузки", async () =>
        {
            if (!IsAdmin) throw new UnauthorizedAccessException("Для чтения BCD откройте расширенную проверку.");
            var bcd = await Run("bcdedit.exe", "/enum", "{current}");
            foreach (var key in new[]{"useplatformclock","useplatformtick","disabledynamictick","numproc","truncatememory","removememory","tscsyncpolicy"})
            {
                var m = Regex.Match(bcd, @"^\s*" + key + @"[ \t]+([^\r\n]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                var val = m.Groups[1].Value.Trim();
                bool timer = key is "useplatformclock" or "useplatformtick" or "disabledynamictick";
                bool forced = timer && (val.Equals("yes",StringComparison.OrdinalIgnoreCase) || val == "1" || val.Equals("да",StringComparison.OrdinalIgnoreCase));
                list.Add(new(key, !m.Success ? Ok : forced ? Risk : Info, m.Success ? val : "Явное значение отсутствует",
                    timer ? "Принудительный режим таймера может изменить плавность кадров. Сравните один фрагмент демо после перезапуска." :
                    key=="tscsyncpolicy" ? "Политика синхронизации отображается для справки." : "Сверьте загрузочное ограничение с возможностями оборудования.",
                    m.Success && key!="tscsyncpolicy" ? "Сохраните текущее значение BCD и убедитесь, что доступен ключ восстановления BitLocker, если шифрование включено. Для возврата выбора Windows администратор может удалить значение: bcdedit /deletevalue {current} " + key + " (в cmd). Перезагрузите ПК. Для отката используйте bcdedit /set {current} " + key + " " + val + " и снова перезагрузитесь." : null, forced ? key : null));
            }
        });
        await Group("Память и файл подкачки", async () =>
        {
            using var doc = JsonDocument.Parse(await PS("$p=Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory;$f=@(Get-CimInstance Win32_PageFileUsage);@{used=[uint64]$p.CommittedBytes;limit=[uint64]$p.CommitLimit;page=@($f | ForEach-Object {$_.AllocatedBaseSize})} | ConvertTo-Json -Compress"));
            var root=doc.RootElement;
            double used=root.GetProperty("used").GetDouble(), limit=root.GetProperty("limit").GetDouble();
            if(limit<=0) throw new IOException("Счётчик предела памяти недоступен.");
            var page=root.GetProperty("page").EnumerateArray().Sum(x=>x.GetInt64());
            list.Add(new("Предел выделения памяти", used/limit>=0.9 ? Risk : page==0 ? Info : Ok,
                string.Format("{0:F1} из {1:F1} ГиБ · подкачка {2} МиБ",used/1073741824,limit/1073741824,page),
                "Повторите сканирование во время нагрузки. При исчерпании лимита игра может зависнуть или закрыться.",
                used/limit>=0.9 || page==0 ? "Свойства системы → Дополнительно → Быстродействие → Дополнительно → Виртуальная память. Запишите исходный режим, включите автоматический выбор и перезагрузите ПК." : null));
        });
        await Group("Питание процессора", async () =>
        {
            foreach(var key in new[]{"PROCTHROTTLEMAX","PROCFREQMAX","PERFBOOSTMODE"})
            {
                var output = await Run("powercfg.exe","/qh","SCHEME_CURRENT","SUB_PROCESSOR",key);
                var values=Regex.Matches(output,@"0x([0-9a-fA-F]{8})\s*$",RegexOptions.Multiline);
                if(values.Count<2) throw new IOException("Формат данных powercfg не распознан.");
                int ac=Convert.ToInt32(values[^2].Groups[1].Value,16), dc=Convert.ToInt32(values[^1].Groups[1].Value,16);
                bool limited=key switch {"PROCTHROTTLEMAX"=>ac<100,"PROCFREQMAX"=>ac>0,_=>ac==0};
                list.Add(new(key, limited?Info:Ok, $"От сети: {ac} · от батареи: {dc}",key switch {
                    "PROCFREQMAX"=>"Предел частоты в МГц. Ноль означает отсутствие явного ограничения.",
                    "PERFBOOSTMODE"=>"Режим ускорения процессора. Ноль отключает буст.",
                    _=>"Верхнее состояние производительности в процентах. Учитывайте режим охлаждения ноутбука."},
                    limited?"Сохраните значения текущего плана. Откройте настройки электропитания и сравните с планом производителя. Проверьте частоту под нагрузкой.":null));
            }
        });
        await Group("Восстановление видеодрайвера", () =>
        {
            foreach(var key in Recovery.GraphicsKeys)
            {
                string? val=Value(Graphics,key);
                list.Add(new(key,val==null?Ok:key=="TdrLevel" && val=="0"?Risk:Info,val??"Системное значение",
                    "Переопределение меняет обнаружение или время ожидания сбоя GPU. Изменение требует перезагрузки.",null,val==null?null:key));
            }
            return Task.CompletedTask;
        });
        await Group("Службы Windows",async () =>
        {
            string[] names=["SysMain","wuauserv","WinDefend","XblAuthManager","XblGameSave","XboxNetApiSvc"];
            var data=await PS("Get-CimInstance Win32_Service -Filter \"Name='SysMain' OR Name='wuauserv' OR Name='WinDefend' OR Name='XblAuthManager' OR Name='XblGameSave' OR Name='XboxNetApiSvc'\" | Select-Object Name,StartMode,State,ExitCode | ConvertTo-Json -Compress");
            using var doc=JsonDocument.Parse(string.IsNullOrWhiteSpace(data)?"[]":data);
            var records=doc.RootElement.ValueKind==JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : new[]{doc.RootElement};
            foreach(var name in names)
            {
                var svc=records.FirstOrDefault(e=>e.GetProperty("Name").GetString()==name);
                bool missing=svc.ValueKind==JsonValueKind.Undefined;
                string mode=missing?"":svc.GetProperty("StartMode").GetString()??"", state=missing?"":svc.GetProperty("State").GetString()??"";
                long exit=missing?0:svc.GetProperty("ExitCode").GetInt64();
                bool fault=exit!=0 && exit!=1077;
                bool review=missing || mode=="Disabled" || fault;
                list.Add(new(name, fault?Risk:review?Info:Ok,missing?"Служба не найдена":$"{mode} · {state} · код {exit}",
                    name=="WinDefend"?"Пассивный режим возможен при стороннем антивирусе. Проверьте поставщика защиты.":
                    name=="SysMain"?"Отключение и временная остановка — разные состояния. Сравните дисковую нагрузку в проблемной сцене.":
                    "Служба может запускаться по запросу. Код ошибки требует проверки в журнале «Система»; задержки DPC таким способом не измеряются.",
                    review? name=="WinDefend"?"Откройте «Безопасность Windows» → поставщики безопасности. Проверьте действующий антивирус и рекомендации Windows.":
                    name=="wuauserv"?"Откройте Центр обновления Windows, выполните поиск обновлений и запустите встроенное устранение неполадок.":
                    name=="SysMain"?"Откройте services.msc → SysMain. Запишите тип запуска. Для сравнения включите автоматический запуск и запустите службу. Повторите тест.":"Проверьте вход в приложение Xbox. В параметрах установленных приложений откройте Xbox → Дополнительные параметры → Исправить.":null));
            }
        });
        await Group("Game Bar и запись игр", async () =>
        {
            var pkg=(await PS("$x=Get-AppxPackage -Name Microsoft.XboxGamingOverlay;if($x){$x.Status.ToString()}else{'Absent'}")).Trim();
            var dvr=Registry.GetValue(@"HKEY_CURRENT_USER\System\GameConfigStore","GameDVR_Enabled",null);
            var policy=Value(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR","AllowGameDVR");
            list.Add(new("Xbox Game Bar",pkg=="Ok"?Ok:Info,pkg=="Absent"?"Пакет отсутствует у пользователя":pkg,
                "Проверьте открытие Win+G. Некоторые игровые профили используют распознавание игр. Наличие пакета само по себе не измеряет работу планировщика.",
                pkg=="Ok"?null:"Параметры → Приложения → Game Bar → Дополнительные параметры → Исправить. Если пакет удалён, установите Xbox Game Bar из Microsoft Store."));
            list.Add(new("GameDVR",Info,$"Запись: {dvr??"по умолчанию"} · политика: {policy??"не задана"}","Отключение записи может быть сознательным выбором. При проблемах с захватом проверьте параметры игр Windows.",
                "Параметры Windows → Игры → Записи. Включите нужную функцию записи и проверьте короткий клип. На управляемом ПК политику меняет администратор."));
        });
        await Group("DNS и приоритет процессов", () =>
        {
            foreach(var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType!=NetworkInterfaceType.Loopback))
                list.Add(new("DNS · "+nic.Name,Info,string.Join(", ",nic.GetIPProperties().DnsAddresses.Select(a=>a.ToString()).DefaultIfEmpty("Адреса не указаны")),
                    "При долгом открытии сайтов сравните время DNS и первого ответа. VPN и защищённый DNS браузера могут использовать другой резолвер."));
            foreach(var name in new[]{"cs2","dota2","Discord","steam","javaw","RobloxPlayerBeta"})
            foreach(var p in Process.GetProcessesByName(name))
            using(p)
            {
                ProcessPriorityClass priority;
                try { priority=p.PriorityClass; } catch(InvalidOperationException){ continue; }
                if(priority==ProcessPriorityClass.RealTime)
                    list.Add(new(name+" · Realtime",Risk,"Процесс работает в реальном времени","При нагрузке возможны задержки ввода и системных задач.",
                        "Диспетчер задач → Подробности → нужный процесс → Задать приоритет → Обычный. Проверьте также скрипт запуска."));
            }
            list.Add(new("GlobalTimerResolutionRequests",Info,Value(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel","GlobalTimerResolutionRequests")??"Не задано","Исключён из предупреждений по настройке MPWS."));
            return Task.CompletedTask;
        });
        return list;
    }
}

