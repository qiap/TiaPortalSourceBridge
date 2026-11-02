using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TiaPortalSourceBridge.Alarms.Native;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Alarms
{
    internal interface IAlarmUiBridge
    {
        void Export(string projectPath, string hmiName, int processId, string path, int timeoutMs);
        void Import(string projectPath, string hmiName, int processId, string path, int timeoutMs);
    }
    // Product UI adapter. No fixed screen coordinates, clipboard, security-dialog handling or automatic
    // retries of an import. Missing accessibility support returns a bounded, actionable failure.
    internal sealed class AlarmUiBridge : IAlarmUiBridge
    {
        private static readonly string[] ExportTitles = { "导出 HMI 报警", "Export HMI alarms" };
        private static readonly string[] ImportTitles = { "导入 HMI 报警", "Import HMI alarms" };
        private static readonly string[] ExportNames = { "导出", "Export" };
        private static readonly string[] ImportNames = { "导入", "Import" };
        private static readonly string[] OkNames = { "确定", "OK" };
        private DateTime deadline;
        private int pid;
        private string stage;
        private AutomationElement main;
        private AutomationElement workspace;
        private AutomationElement editor;
        private IntPtr mainHandle;
        private string[] titles;
        private static string Name(AutomationElement element) { return element.Current.Name ?? ""; }
        private static string Caption(AutomationElement element)
        {return System.Text.RegularExpressions.Regex.Replace(Name(element),@"\s*[（(]&?[A-Za-z][)）]", "").Replace("&", "").Trim();}
        private IEnumerable<AutomationElement> Controls(AutomationElement root)
        {
            // TIA's grid provider eagerly materializes every cell on a Descendants query.
            // Walk container children and stop at grids: locating a toolbar must not enumerate alarms.
            var pending=new Queue<AutomationElement>();pending.Enqueue(root);int count=0;
            while(pending.Count>0)
            {
                if(DateTime.UtcNow>=deadline)throw new TimeoutException("报警控件定位超时："+stage);
                var next=pending.Dequeue();
                int type;
                try { type=next.Current.ControlType; }
                catch (COMException) { continue; }
                yield return next;
                if(type==ControlType.DataGrid||type==ControlType.Table||type==ControlType.Image||type==ControlType.Tree
                    ||type==ControlType.Button||type==ControlType.CheckBox||type==ControlType.Edit||type==ControlType.TabItem||type==50020||type==50011)continue;
                AutomationElement[] children;
                children = next.FindAll(TreeScope.Children,Condition.TrueCondition).Cast<AutomationElement>().ToArray();
                foreach(AutomationElement child in children)pending.Enqueue(child);
                if(++count>2000)throw new InvalidOperationException("博途控件结构超出已支持范围，未发送输入。");
            }
        }
        private AutomationElement[] All(AutomationElement element, int type)
        {
            if(type==ControlType.Edit&&(element.Current.ControlType==ControlType.DataGrid||element.Current.ControlType==ControlType.Table))
                return element.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,type)).Cast<AutomationElement>().ToArray();
            return Controls(element).Where(e=>{ try { return e.Current.ControlType==type; } catch (COMException) { return false; } }).ToArray();
        }
        private AutomationElement ById(AutomationElement root, string id)
        {
            try {
                var direct = root.FindFirst(TreeScope.Descendants, new PropertyCondition(30011, id));
                if (direct != null && !direct.Current.IsOffscreen) return direct;
            } catch (COMException) { OperationMetrics.Count("alarm_lookup_fallbacks"); }
            foreach(var e in Controls(root))
            {
                try { if(e.Current.AutomationId==id&&!e.Current.IsOffscreen)return e; }
                catch (COMException) { }
            }
            return null;
        }
        private AutomationElement OpenTab(AutomationElement status, string exact)
        {
            try {
                var direct = status.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, exact));
                if (direct != null) return direct;
            } catch (COMException) { OperationMetrics.Count("alarm_lookup_fallbacks"); }
            return All(status, ControlType.Button).FirstOrDefault(e => Name(e) == exact);
        }
        private T Ready<T>(Func<T> read)
        {
            while(true)try{return read();}
            catch(TimeoutException){Tick();}
            catch(COMException){Tick();}
        }
        private static string Value(AutomationElement e)
        { string cached=e.CachedValue;if(cached!=null)return cached;object p; return e.TryGetCurrentPattern(ValuePattern.Pattern, out p) ? ((ValuePattern)p).Current.Value : Name(e); }
        private static string Label(AutomationElement e)
        { string v = Value(e); int pos = v.IndexOf(";Text=", StringComparison.Ordinal); return pos < 0 ? v : v.Substring(pos + 6); }
        private static int Indent(AutomationElement e)
        { string v = Value(e); int end = v.IndexOf(';'); int n; return end > 12 && int.TryParse(v.Substring(12, end - 12), out n) ? n : 0; }
        [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { internal int dx, dy; internal uint data, flags, time; internal IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KeyInput { internal ushort vk, scan; internal uint flags, time; internal IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] internal MouseInput mouse; [FieldOffset(0)] internal KeyInput key; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { internal uint type; internal InputUnion u; }
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
        [DllImport("user32.dll", SetLastError=true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        private delegate bool EnumWindow(IntPtr handle,IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback,IntPtr state);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle,StringBuilder text,int length);
        private void Foreground()
        {
            uint foregroundPid;
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out foregroundPid);
            if (foregroundPid == pid) return;
            uint thisThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);
            try { BringWindowToTop(mainHandle); SetForegroundWindow(mainHandle); }
            finally { if (attached) AttachThreadInput(thisThread, foregroundThread, false); }
        }
        private void Pointer(AutomationElement element, bool twice)
        {
            Foreground();
            var r = element.Current.BoundingRectangle;
            if (element.Current.IsOffscreen || r.IsEmpty || r.Width < 1 || r.Height < 1) throw new InvalidOperationException("目标控件不可见：" + Name(element));
            uint target; var point = new Point { X = (int)(r.Left + r.Width / 2), Y = (int)(r.Top + r.Height / 2) };
            GetWindowThreadProcessId(WindowFromPoint(point), out target);
            if (target != pid) throw new InvalidOperationException("目标控件被其他窗口遮挡，未发送输入。");
            GetWindowThreadProcessId(GetForegroundWindow(), out target);
            if (target != pid) throw new InvalidOperationException("博途没有获得前台焦点，未发送输入。");
            if (!SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("无法定位目标控件。");
            var input = new[] { new Input { type=0,u=new InputUnion { mouse=new MouseInput { flags=2 } } }, new Input { type=0,u=new InputUnion { mouse=new MouseInput { flags=4 } } } };
            if (SendInput(2,input,Marshal.SizeOf(typeof(Input))) != 2) throw new InvalidOperationException("Windows 拒绝界面输入，请确保工具和博途以相同权限运行。");
            if (twice) { Thread.Sleep(70); if (SendInput(2,input,Marshal.SizeOf(typeof(Input))) != 2) throw new InvalidOperationException("Windows 拒绝第二次点击。"); }
        }
        private void Right()
        {
            uint target; GetWindowThreadProcessId(GetForegroundWindow(),out target); if(target!=pid)throw new InvalidOperationException("博途焦点已改变，已停止。");
            var input=new[]{new Input{type=1,u=new InputUnion{key=new KeyInput{vk=0x27}}},new Input{type=1,u=new InputUnion{key=new KeyInput{vk=0x27,flags=2}}}};
            if(SendInput(2,input,Marshal.SizeOf(typeof(Input)))!=2)throw new InvalidOperationException("Windows 拒绝展开节点输入。");
        }
        private void Tick()
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("报警界面操作超时（" + stage + "）。请查看目标博途是否有弹窗，或打开对应 HMI 报警编辑器后重试；未自动重复导入。");
            Thread.Sleep(100);
        }
        private AutomationElement[] Windows()
        {
            var handles=new List<Tuple<IntPtr,string>>();
            EnumWindows((h,s)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==pid&&IsWindowVisible(h)){var title=new StringBuilder(1024);GetWindowText(h,title,title.Capacity);handles.Add(Tuple.Create(h,title.ToString()));}return true;},IntPtr.Zero);
            var list = new List<AutomationElement>();
            foreach (var h in handles.Where(x => x.Item1 != mainHandle))
            {
                try { list.Add(AutomationElement.FromHandle(h.Item1, h.Item2)); }
                catch (COMException) { }
            }
            return list.ToArray();
        }
        private AutomationElement WaitWindow(string[] names)
        {
            while (true)
            {
                var windows = Windows();
                var found = windows.Where(w => names.Any(n => string.Equals(Name(w), n, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (found.Length == 1) return found[0];
                foreach (var w in windows)
                {
                    string title = Name(w);
                    if (title.IndexOf("失败", StringComparison.OrdinalIgnoreCase) >= 0 || title.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new InvalidOperationException("博途报警操作失败：" + title + "；请查看工程 Logs 中的报警导入日志。");
                }
                Tick();
            }
        }
        private static void Invoke(AutomationElement element)
        {
            object pattern;
            if (!element.Current.IsEnabled) throw new InvalidOperationException("目标控件不可用：" + Name(element));
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) { ((InvokePattern)pattern).Invoke(); return; }
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) { ((SelectionItemPattern)pattern).Select(); return; }
            throw new InvalidOperationException("控件未提供可调用的操作：" + Name(element));
        }
        private void Button(AutomationElement root, string[] names)
        {
            var found = All(root, ControlType.Button).Where(e => e.Current.IsEnabled && (
                string.Equals(Name(e), "OKButton", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Current.AutomationId, "OKButton", StringComparison.OrdinalIgnoreCase) ||
                names.Any(n => string.Equals(Caption(e), n, StringComparison.OrdinalIgnoreCase)))).ToArray();
            if (found.Length != 1) throw new InvalidOperationException("无法唯一定位按钮：" + string.Join("/", names)+"；实际按钮："+string.Join("；",All(root,ControlType.Button).Select(e=>Name(e)+" ["+e.Current.AutomationId+"] enabled="+e.Current.IsEnabled)));
            Invoke(found[0]);
        }
        private void Select(string projectPath, string hmiName, int processId, int timeoutMs)
        {
            deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs); stage = "选择工程";
            Console.Error.WriteLine("[alarm-ui] " + stage);
            if (string.IsNullOrWhiteSpace(hmiName)) throw new ArgumentException("界面报警操作必须提供 hmi_name（项目树中的 HMI 名称）。");
            string identity = Path.ChangeExtension(Path.GetFullPath(projectPath), null);
            var allPortals = Process.GetProcessesByName("Siemens.Automation.Portal").Where(p => p.MainWindowHandle != IntPtr.Zero && (processId == 0 || p.Id == processId)).ToArray();
            var candidates = allPortals.Where(p => p.MainWindowTitle.IndexOf(identity, StringComparison.OrdinalIgnoreCase) >= 0
                || p.MainWindowTitle.IndexOf(projectPath, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (candidates.Length != 1) throw new InvalidOperationException("没有唯一匹配已打开工程的博途窗口。请打开目标工程，多个窗口时明确 process_id。");
            pid = candidates[0].Id; mainHandle=candidates[0].MainWindowHandle; main = Ready(()=>AutomationElement.FromHandle(mainHandle));
            Console.Error.WriteLine("[alarm-ui] 窗口 " + pid);
            object win;
            if (main.TryGetCurrentPattern(WindowPattern.Pattern, out win) && ((WindowPattern)win).Current.WindowVisualState == WindowVisualState.Minimized)
                ((WindowPattern)win).SetWindowVisualState(WindowVisualState.Normal);
            if (!main.Current.IsEnabled) throw new InvalidOperationException("目标博途被对话框阻塞，请先处理弹窗。");
            Foreground();
            workspace=Ready(()=>main.FindFirst(TreeScope.Children,new PropertyCondition(AutomationElement.NameProperty,"PortalAndWorkFrame")))??main;
            stage = "定位 HMI 报警编辑器";
            Console.Error.WriteLine("[alarm-ui] " + stage);
            string prefix=Path.GetFileNameWithoutExtension(projectPath)+"\\"+hmiName+"\\";
            var status=Ready(()=>workspace.FindFirst(TreeScope.Children,new PropertyCondition(AutomationElement.NameProperty,"Siemens.Automation.FrameApplication.StatusBarView")));
            if(status!=null)
            {
                string exact=prefix+"Siemens.Simatic.Hmi.Global:Siemens.Simatic.Hmi.Utah.Alarm.FolderService:DisplayName";
                var openTab=Ready(()=>OpenTab(status,exact));
                var openTabs=openTab==null?new AutomationElement[0]:new[]{openTab};
                if(openTabs.Length==1)
                {
                    Ready(()=>{Invoke(openTabs[0]);return true;});
                    var openFrame=Ready(()=>workspace.FindFirst(TreeScope.Children,new PropertyCondition(AutomationElement.NameProperty,"Siemens.Automation.FrameApplication.EditorMainAndInspectorFrame")));
                    if(openFrame!=null&&(editor=Ready(()=>ById(openFrame,"Siemens.Simatic.Hmi.Alarm.Snapins.AlarmEditorContainer")))!=null){Console.Error.WriteLine("[alarm-ui] 复用目标报警编辑器");return;}
                }
            }
            var navigation=Ready(()=>workspace.FindFirst(TreeScope.Children,new PropertyCondition(AutomationElement.NameProperty,"Siemens.Automation.FrameApplication.ApplicationNavigationContainer")));
            var grid=Ready(()=>ById(navigation??main,"ProjectNavigationTree"));
            Console.Error.WriteLine("[alarm-ui] 项目树已读取");
            if(grid==null)throw new InvalidOperationException("请将博途切换到项目视图，未找到项目树。");
            var gridRows=All(grid,ControlType.Edit);
            Console.Error.WriteLine("[alarm-ui] 项目树行 " + gridRows.Length);
            var hmis = gridRows.Where(e => Label(e) == hmiName || Label(e).StartsWith(hmiName + " [", StringComparison.Ordinal)).ToArray();
            if (hmis.Length != 1) throw new InvalidOperationException("无法在项目树中唯一定位 HMI：" + hmiName + "。请展开对应 HMI 设备；不会根据坐标盲点。");
            Pointer(hmis[0],false); Right();
            Console.Error.WriteLine("[alarm-ui] HMI 已选择");
            AutomationElement alarmNode = null;
            while (alarmNode == null)
            {
                gridRows=All(grid,ControlType.Edit);
                int index=Array.FindIndex(gridRows,e=>Label(e)==hmiName||Label(e).StartsWith(hmiName+" [",StringComparison.Ordinal));
                if(index<0)throw new InvalidOperationException("HMI 节点在操作中发生变化。");
                int indent=Indent(gridRows[index]);
                var matches=gridRows.Skip(index+1).TakeWhile(e=>Indent(e)>indent).Where(e=>new[]{"HMI 报警","HMI alarms"}.Contains(Label(e))).ToArray();
                if (matches.Length == 1) alarmNode = matches[0]; else Tick();
            }
            Pointer(alarmNode,true);
            Console.Error.WriteLine("[alarm-ui] 报警节点已打开");
            var frame=Ready(()=>workspace.FindFirst(TreeScope.Children,new PropertyCondition(AutomationElement.NameProperty,"Siemens.Automation.FrameApplication.EditorMainAndInspectorFrame")));
            while((editor=Ready(()=>ById(frame??main,"Siemens.Simatic.Hmi.Alarm.Snapins.AlarmEditorContainer")))==null)Tick();
            Console.Error.WriteLine("[alarm-ui] 编辑器已定位");
            status=Ready(()=>workspace.FindFirst(TreeScope.Children,new PropertyCondition(AutomationElement.NameProperty,"Siemens.Automation.FrameApplication.StatusBarView")));
            Console.Error.WriteLine("[alarm-ui] 切换栏已定位");
            if(status==null)throw new InvalidOperationException("未找到博途编辑器切换栏，无法核对目标 HMI。");
            var checkedTab=Ready(()=>OpenTab(status,prefix+"Siemens.Simatic.Hmi.Global:Siemens.Simatic.Hmi.Utah.Alarm.FolderService:DisplayName"));
            var targetTabs=checkedTab==null?new AutomationElement[0]:new[]{checkedTab};
            if(targetTabs.Length!=1)throw new InvalidOperationException("HMI 报警编辑器的工程路径未匹配目标。");
            Console.Error.WriteLine("[alarm-ui] HMI 标题已核对");
        }
        private void OpenDialog(string[] dialogTitles, bool exporting)
        {
            titles = dialogTitles;
            // Toolbar tooltips are exposed as accessible names by supported TIA builds.
            // Reacquire each step; do not retain element handles across editor replacement.
            stage = exporting ? "打开报警导出" : "打开报警导入";
            Console.Error.WriteLine("[alarm-ui] " + stage);
            string[] names = exporting ? new[] { "导出报警", "导出", "导出 HMI 报警", "Export alarms", "Export", "Export HMI alarms" }
                : new[] { "导入报警", "导入", "导入 HMI 报警", "Import alarms", "Import", "Import HMI alarms" };
            AutomationElement[] controls=null;
            var editors = Ready(()=> {
                controls=Controls(editor).ToArray();
                var items = controls.Where(e=>e.Current.ControlType==ControlType.TabItem).Where(e => Name(e) == "Tab_HmiEditorPage01.DiscreteAlarmEditor" || Name(e) == "离散量报警" || Name(e) == "Discrete alarms").ToArray();
                if(items.Length==0)throw new TimeoutException(); return items;
            });
            if (editors.Length != 1) throw new InvalidOperationException("未唯一定位离散报警编辑器；实际选项卡：" + string.Join("；", All(editor, ControlType.TabItem).Select(e => Name(e) + " [" + e.Current.AutomationId + "]")));
            bool selected=Ready(()=>{object selection;return editors[0].TryGetCurrentPattern(SelectionItemPattern.Pattern,out selection)&&((SelectionItemPattern)selection).IsSelected;});
            if(!selected){Invoke(editors[0]);controls=Ready(()=>Controls(editor).ToArray());}
            string resource=exporting?"Hmi.ImportExport.Export_ImportExport.Export.Icon":"Hmi.ImportExport.Import_ImportExport.Import.Icon";
            AutomationElement[] buttons;
            while(true){buttons=controls.Where(e=>e.Current.ControlType==ControlType.Button&&!e.Current.IsOffscreen&&(Name(e)==resource||names.Contains(Name(e)))).ToArray();if(buttons.Length==1)break;Tick();controls=Ready(()=>Controls(editor).ToArray());}
            if (buttons.Length != 1) throw new InvalidOperationException("未唯一定位报警导入/导出按钮，当前博途界面布局不受支持。");
            Invoke(buttons[0]);
            Console.Error.WriteLine("[alarm-ui] 已调用报警导入导出按钮");
        }
        private AutomationElement SetPath(string path)
        {
            stage = "填写报警文件路径";
            var dialog = WaitWindow(titles);
            var edits = Ready(() =>
            {
                var found = All(dialog, ControlType.Edit).Where(e => e.Current.IsEnabled).ToArray();
                if (found.Length != 1) throw new TimeoutException();
                return found;
            });
            object value;
            if (!edits[0].TryGetCurrentPattern(ValuePattern.Pattern, out value)) throw new InvalidOperationException("报警路径输入框不支持自动填入。");
            ((ValuePattern)value).SetValue(Path.GetFullPath(path));
            if (((ValuePattern)value).Current.Value != Path.GetFullPath(path)) throw new InvalidOperationException("报警文件路径回读不一致。");
            return dialog;
        }
        private void Complete(bool exporting)
        {
            stage = exporting ? "等待导出结果" : "等待导入结果";
            while (true)
            {
                foreach (var window in Windows())
                {
                    string title = Name(window);
                    if (title.IndexOf("失败", StringComparison.OrdinalIgnoreCase) >= 0 || title.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new InvalidOperationException("博途报警操作失败：" + title + "；请检查工程 Logs。");
                    if (title.IndexOf(exporting ? "导出完成" : "导入完成", StringComparison.OrdinalIgnoreCase) >= 0
                        || title.IndexOf(exporting ? "导出已完成" : "导入已完成", StringComparison.OrdinalIgnoreCase) >= 0
                        || title.IndexOf(exporting ? "Export completed" : "Import completed", StringComparison.OrdinalIgnoreCase) >= 0)
                    { Ready(() => { Button(window, OkNames); return true; }); return; }
                }
                Tick();
            }
        }
        public void Export(string projectPath, string hmiName, int processId, string path, int timeoutMs)
        {
            if (File.Exists(path)) throw new IOException("报警导出目标必须为新文件，避免覆盖弹窗。");
            using(OperationMetrics.Measure("alarm_ui_select")) Select(projectPath, hmiName, processId, timeoutMs);
            using(OperationMetrics.Measure("alarm_ui_open_dialog")) OpenDialog(ExportTitles, true);
            AutomationElement dialog;
            using(OperationMetrics.Measure("alarm_ui_set_path")) dialog = SetPath(path);
            var checks = Ready(() => All(dialog, ControlType.CheckBox));
            foreach (var check in checks)
            {
                object toggle; if (!check.TryGetCurrentPattern(TogglePattern.Pattern, out toggle)) continue;
                bool discrete = Caption(check) == "离散量报警" || Caption(check) == "Discrete alarms";
                bool analog = Caption(check) == "模拟量报警" || Caption(check) == "Analog alarms";
                if (discrete && ((TogglePattern)toggle).Current.ToggleState != ToggleState.On || analog && check.Current.IsEnabled && ((TogglePattern)toggle).Current.ToggleState == ToggleState.On) ((TogglePattern)toggle).Toggle();
            }
            using(OperationMetrics.Measure("alarm_ui_submit")) Ready(() => { Button(dialog, ExportNames); return true; });
            using(OperationMetrics.Measure("alarm_ui_complete")) Complete(true);
            if (!File.Exists(path)) throw new IOException("博途提示导出完成，但目标文件不存在。");
        }
        public void Import(string projectPath, string hmiName, int processId, string path, int timeoutMs)
        {
            using(OperationMetrics.Measure("alarm_ui_select")) Select(projectPath, hmiName, processId, timeoutMs);
            using(OperationMetrics.Measure("alarm_ui_open_dialog")) OpenDialog(ImportTitles, false);
            AutomationElement dialog;
            using(OperationMetrics.Measure("alarm_ui_set_path")) dialog = SetPath(path);
            stage = "提交报警导入";
            // Invoking import can succeed even if COM reports a timeout; never retry this action.
            using(OperationMetrics.Measure("alarm_ui_submit")) Button(dialog, ImportNames);
            using(OperationMetrics.Measure("alarm_ui_complete")) Complete(false);
        }
    }
}
