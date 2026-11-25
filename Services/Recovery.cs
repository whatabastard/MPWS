using Microsoft.Win32;
using MPWS.Models;

namespace MPWS.Services;

public static class Recovery
{
    internal static readonly string[] GraphicsKeys = ["TdrLevel","TdrDelay","TdrDdiDelay"];
    static readonly string[] Timers=["useplatformclock","useplatformtick","disabledynamictick"];
    public static bool IsTimer(string key)=>Timers.Contains(key);
    static IEnumerable<string> Keys=>GraphicsKeys.Concat(Timers);
    static string Bcd(params string[] args)=>Task.Run(()=>DiagnosticsService.Run("bcdedit.exe",args)).GetAwaiter().GetResult();
    static (string Id,string? Value) BootValue(string name)
    {
        var entry=Bcd("/enum","{current}","/v");
        var id=System.Text.RegularExpressions.Regex.Match(entry,@"\{[0-9a-fA-F-]{36}\}");
        if(!id.Success) throw new InvalidOperationException("Не удалось определить загрузочную запись.");
        var m=System.Text.RegularExpressions.Regex.Match(entry,@"(?im)^\s*"+name+@"[ \t]+([^\r\n]+)");
        return (id.Value,m.Success?m.Groups[1].Value.Trim().ToLowerInvariant():null);
    }
    static void BootChange(string name,bool undo)
    {
        var state=BootValue(name);
        using var root=Registry.LocalMachine.CreateSubKey(Journal);
        using var old=root.OpenSubKey(name);
        bool enabled=state.Value is "yes" or "да" or "1";
        if(undo)
        {
            if(!Equals(old?.GetValue("Entry"),state.Id) || !Equals(old?.GetValue("Original"),1) || (state.Value!=null && !enabled))
                throw new InvalidOperationException("Загрузочная запись или значение изменились. Откат остановлен.");
            Bcd("/set",state.Id,name,"yes");
            if(BootValue(name).Value is not ("yes" or "да" or "1")) throw new InvalidOperationException("Проверка отката не пройдена.");
            root.DeleteSubKeyTree(name);
        }
        else
        {
            if(!enabled || old!=null) throw new InvalidOperationException("Значение изменилось или снимок уже существует.");
            using var k=root.CreateSubKey(name);
            k.SetValue("Entry",state.Id); k.SetValue("Time",DateTime.Now.ToBinary(),RegistryValueKind.QWord);
            k.SetValue("Original",1,RegistryValueKind.DWord); k.Flush();
            Bcd("/deletevalue",state.Id,name);
            if(BootValue(name).Value!=null) throw new InvalidOperationException("Windows сохранила явное значение. Проверьте снимок.");
        }
    }
    const string Journal = @"SOFTWARE\MPWS\Recovery";
    public static List<Change> Pending()
    {
        using var root=Registry.LocalMachine.OpenSubKey(Journal);
        List<Change> items=[];
        foreach(var name in Keys)
        {
            using var k=root?.OpenSubKey(name);
            if(k?.GetValue("Original") is int old)
                items.Add(new(name,old,Environment.MachineName,DateTime.FromBinary((long)(k.GetValue("Time")??0L))));
        }
        return items;
    }
    public static void Apply(string name, bool undo, int expected)
    {
        if(!Keys.Contains(name) || !DiagnosticsService.IsAdmin) throw new InvalidOperationException("Для изменения необходим расширенный режим.");
        using var mutex=new Mutex(false,@"Global\MPWS.Recovery");
        bool acquired;
        try { acquired=mutex.WaitOne(0); } catch(AbandonedMutexException){ acquired=true; }
        if(!acquired) throw new InvalidOperationException("Другая копия MPWS выполняет восстановление.");
        try
        {
            if(IsTimer(name)){ BootChange(name,undo); return; }
            using var gpu=Registry.LocalMachine.OpenSubKey(DiagnosticsService.Graphics,true) ?? throw new InvalidOperationException("Раздел драйвера недоступен.");
            using var root=Registry.LocalMachine.CreateSubKey(Journal);
            using var existing=root.OpenSubKey(name);
            var current=gpu.GetValue(name);
            if(undo)
            {
                if(existing?.GetValue("Original") is not int old || old!=expected) throw new InvalidOperationException("Снимок изменён. Обновите отчёт.");
                if(current!=null && !Equals(current,old)) throw new InvalidOperationException("Параметр изменён другой программой. Откат остановлен.");
                gpu.SetValue(name,old,RegistryValueKind.DWord);
                gpu.Flush();
                if(!Equals(gpu.GetValue(name),old)) throw new InvalidOperationException("Проверка отката не пройдена.");
                root.DeleteSubKeyTree(name);
            }
            else
            {
                if(current is not int old || old!=expected || existing!=null) throw new InvalidOperationException("Значение изменилось или уже есть снимок. Проверьте раздел отката.");
                using var k=root.CreateSubKey(name);
                k.SetValue("Time",DateTime.Now.ToBinary(),RegistryValueKind.QWord);
                k.SetValue("Original",old,RegistryValueKind.DWord);
                k.Flush();
                gpu.DeleteValue(name);
                gpu.Flush();
                if(gpu.GetValue(name)!=null) throw new InvalidOperationException("Проверка записи не пройдена.");
            }
        }
        finally { mutex.ReleaseMutex(); }
    }
}

