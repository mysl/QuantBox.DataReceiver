﻿using FileMonitor;
using Newtonsoft.Json;
using NLog;
using QuantBox;
using QuantBox.XAPI;
using QuantBox.XAPI.Callback;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace DataReceiver
{
    public class DataReceiver:ApiBase
    {
        public DRTickWriter TickWriter;

        public List<ConnectionConfig> ConnectionConfigList;
        public List<InstrumentFilterConfig> IncludeFilterList;
        public List<InstrumentFilterConfig> ExcludeFilterList;
        public List<Tuple<TimeSpan, string, string>> ScheduleTasksList;

        public string ConnectionConfigListFileName = @"ConnectionConfigList.json";
        public string IncludeFilterListFileName = @"IncludeFilterList.json";
        public string ExcludeFilterListFileName = @"ExcludeFilterList.json";

        public string SaveAsInstrumentInfoListFileName = @"InstrumentInfoList.json";
        public string SaveAsTradingDayFileName = @"TradingDay.json";

        public string ScheduleTasksListFileName = @"ScheduleTasks.json";

        private System.Timers.Timer _Timer = new System.Timers.Timer();

        public ActionBlock<DepthMarketDataNClass> Input;
        private Logger Log = LogManager.GetCurrentClassLogger();

        #region 配置文件重新加载
        private Dictionary<string, Tuple<FileSystemWatcher, WatcherTimer>> watchers = new Dictionary<string, Tuple<FileSystemWatcher, WatcherTimer>>();

        public void WatcherStrat(string path, string filter)
        {
            string key = Path.Combine(path, filter);
            Tuple<FileSystemWatcher, WatcherTimer> tuple;
            if (!watchers.TryGetValue(key, out tuple))
            {
                WatcherTimer timer = new WatcherTimer(OnReload);

                FileSystemWatcher watcher = new FileSystemWatcher();
                watcher.Path = path;
                watcher.Filter = filter;
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
                watcher.Changed += new FileSystemEventHandler(timer.OnFileChanged);
                watcher.Deleted += new FileSystemEventHandler(timer.OnFileChanged);
                watcher.EnableRaisingEvents = true;

                tuple = new Tuple<FileSystemWatcher, WatcherTimer>(watcher,timer);
            }
            watchers[key] = tuple;
        }

        public void WatcherStop()
        {
            foreach (var watcher in watchers.Values)
            {
                watcher.Item1.EnableRaisingEvents = false;
            }
            watchers.Clear();
        }

        /// <summary>
        /// 文件改变事件
        /// </summary>
        /// <param name="source"></param>
        /// <param name="e"></param>
        private void OnReload(object source, FileSystemEventArgs e)
        {
            // 只关注这三个文件的变化
            if(e.FullPath.EndsWith(InstrumentInfoListFileName)
                || e.FullPath.EndsWith(IncludeFilterListFileName)
                || e.FullPath.EndsWith(ExcludeFilterListFileName)
                )
            {
                Log.Info("文件变动{0},{1}", e.ChangeType, e.FullPath);
                if (e.ChangeType == WatcherChangeTypes.Changed)
                {
                    ProcessConfig(e.FullPath);
                }
                else if (e.ChangeType == WatcherChangeTypes.Deleted)
                {
                    //CreateConfig(e.FullPath);
                }
            }
            else if (e.FullPath.EndsWith(ScheduleTasksListFileName))
            {
                ProcessScheduleTasks(e.FullPath);
            }
        }

        /// <summary>
        /// 如果加载失败就生成
        /// </summary>
        /// <param name="fullPath"></param>
        public void ProcessConfig(string fullPath)
        {
            List<InstrumentInfo> oldList = new List<InstrumentInfo>(InstrumentInfoList);

            LoadInstrumentInfoList();
            // 得到要订阅的表，是否覆盖原表？
            List<InstrumentInfo> newList = Filter(InstrumentInfoList);
            InstrumentInfoList = newList;

            IEnumerable<InstrumentInfo> _have = newList.Intersect(oldList);
            // 对于已经订阅的，有可能合约最小变动价位变化，所以可能需要重新更新
            foreach(var h in _have)
            {
                AddInstrument(h);
            }

            IEnumerable<InstrumentInfo> _old = oldList.Except(newList);
            Unsubscribe(_old);
            IEnumerable<InstrumentInfo> _new = newList.Except(oldList);
            int f = Subscribe(_new);

            Log.Info("取消订阅:{0},尝试订阅:{1},已经订阅:{2},订阅失败:{3}", _old.Count(), _new.Count(), _have.Count(), f);

            SaveAsInstrumentInfoList();
        }

        public void ProcessScheduleTasks(string fullPath)
        {
            LoadScheduleTasks();

            //ScheduleTasksList.Add(new Tuple<TimeSpan, string>(new TimeSpan(08, 50, 0), "notepad.exe"));
            //ScheduleTasksList.Add(new Tuple<TimeSpan, string>(new TimeSpan(08, 50, 0), "notepad.exe"));

            //Save(ConfigPath, ScheduleTasksListFileName, ScheduleTasksList);
            _Timer.Elapsed -= _Timer_Elapsed;
            _Timer.Elapsed += _Timer_Elapsed;
            _Timer.Interval = 10 * 1000;
            _Timer.Enabled = true;
        }

        int CheckedTime = -1;

        void _Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            _Timer.Enabled = false;

            int now = e.SignalTime.Hour*100+e.SignalTime.Minute;
            if (CheckedTime == now)
            {
                _Timer.Enabled = true;
                return;
            }
            CheckedTime = now;

            foreach(var it in ScheduleTasksList)
            {
                DateTime dt = e.SignalTime.Date.Add(it.Item1);
                int t = dt.Hour*100+dt.Minute;
                if(t == now)
                {
                    Log.Info("执行任务:{0} {1} {2}", it.Item1, it.Item2, it.Item3);
                    var proc = Process.Start(it.Item2, it.Item3);
                    //proc.WaitForExit();
                    break;
                }
            }

            _Timer.Enabled = true;
        }
        #endregion

        public DataReceiver()
        {
            Input = new ActionBlock<DepthMarketDataNClass>((x) => OnInputMarketData(x));
        }

        public void Save()
        {
            Save(ConfigPath, ConnectionConfigListFileName, ConnectionConfigList);
            Save(ConfigPath, InstrumentInfoListFileName, InstrumentInfoList);
            Save(ConfigPath, IncludeFilterListFileName, IncludeFilterList);
            Save(ConfigPath, ExcludeFilterListFileName, ExcludeFilterList);
        }

        /// <summary>
        /// 这里保存的是过滤后的合约列表，也就是正在订阅的合约列表,保存在其它地方可供用户下载或检测
        /// </summary>
        public void SaveAsInstrumentInfoList()
        {
            Save(DataPath, SaveAsInstrumentInfoListFileName, InstrumentInfoList);
        }

        public void SaveAsTradingDay()
        {
            Save(DataPath, SaveAsTradingDayFileName, TradingDay);
        }

        public void LoadConnectionConfig()
        {
            ConnectionConfigList = new List<ConnectionConfig>();

            ConnectionConfigList = (List<ConnectionConfig>)Load(ConfigPath, ConnectionConfigListFileName, ConnectionConfigList);

            if (ConnectionConfigList == null)
                ConnectionConfigList = new List<ConnectionConfig>();
        }

        public void LoadInstrumentInfoList()
        {
            InstrumentInfoList = new List<InstrumentInfo>();
            IncludeFilterList = new List<InstrumentFilterConfig>();
            ExcludeFilterList = new List<InstrumentFilterConfig>();

            InstrumentInfoList = (List<InstrumentInfo>)Load(ConfigPath, InstrumentInfoListFileName, InstrumentInfoList);
            IncludeFilterList = (List<InstrumentFilterConfig>)Load(ConfigPath, IncludeFilterListFileName, IncludeFilterList);
            ExcludeFilterList = (List<InstrumentFilterConfig>)Load(ConfigPath, ExcludeFilterListFileName, ExcludeFilterList);

            if (InstrumentInfoList == null)
                InstrumentInfoList = new List<InstrumentInfo>();
            if (IncludeFilterList == null)
                IncludeFilterList = new List<InstrumentFilterConfig>();
            if (ExcludeFilterList == null)
                ExcludeFilterList = new List<InstrumentFilterConfig>();
        }

        public void LoadScheduleTasks()
        {
            ScheduleTasksList = new List<Tuple<TimeSpan, string, string>>();

            ScheduleTasksList = (List<Tuple<TimeSpan, string, string>>)Load(ConfigPath, ScheduleTasksListFileName, ScheduleTasksList);
            if (ScheduleTasksList == null)
                ScheduleTasksList = new List<Tuple<TimeSpan, string, string>>();

            Log.Info("加载计划任务数:{0}", ScheduleTasksList.Count);
        }

        public void Connect()
        {
            // 查看有多少种连接
            int j = 0;
            foreach (var cc in ConnectionConfigList)
            {
                
                // 建立多个连接
                for (int i = 0; i < cc.SessionLimit; ++i)
                {
                    XApi api = new XApi(cc.LibPath);
                    api.Server = cc.Server;
                    api.User = cc.User;
                    api.Log = LogManager.GetLogger(string.Format("{0}.{1}.{2}.{3}", api.Server.BrokerID, api.User.UserID, j, i));
            
                    api.MaxSubscribedInstrumentsCount = cc.SubscribePerSession;

                    api.OnConnectionStatus = OnConnectionStatus;
                    api.OnRtnDepthMarketData = OnRtnDepthMarketData;

                    api.Connect();

                    XApiList.Add(api);
                }
                ++j;
            }
        }

        public bool Contains(string szInstrument, string szExchange)
        {
            foreach (var api in XApiList)
            {
                if (api.SubscribedInstrumentsContains(szInstrument,szExchange))
                {
                    return true;
                }
            }
            return false;
        }

        public List<InstrumentInfo> Filter(List<InstrumentInfo> list)
        {
            List<InstrumentInfo> newList = new List<InstrumentInfo>();

            foreach (var i in list)
            {
                // 查看是否需要订阅
                InstrumentFilterConfig match1 = Match(i.Symbol, IncludeFilterList);

                if (match1 != null)
                {
                    i.Time_ssf_Diff = match1.Time_ssf_Diff;
                    // 包含，需要订阅

                    InstrumentFilterConfig match2 = Match(i.Symbol, ExcludeFilterList);
                    if (match2 == null)
                    {
                        newList.Add(i);
                        //Console.WriteLine("合约{0}匹配于{1}排除列表", i.Symbol, ExcludeFilterListFileName);
                    }
                    else
                    {
                        //Console.WriteLine("合约{0}匹配于{1}排除列表", i.Symbol, ExcludeFilterListFileName);
                    }
                }
                else
                {
                    //Console.WriteLine("合约{0}不匹配于{1}包含列表", i.Symbol, IncludeFilterListFileName);
                }
            }

            return newList;
        }

        public void AddInstrument(InstrumentInfo i)
        {
            TickWriter.AddInstrument(string.Format("{0}.{1}", i.Instrument, i.Exchange), i.TickSize, i.Factor, i.Time_ssf_Diff);
            TickWriter.AddInstrument(string.Format("{0}.", i.Instrument), i.TickSize, i.Factor, i.Time_ssf_Diff);
            //TickWriter.AddInstrument(string.Format("{0}", i.Instrument), i.TickSize, i.Factor, i.Time_ssf_Diff);
        }

        public void RemoveInstrument(InstrumentInfo i)
        {
            TickWriter.RemoveInstrument(string.Format("{0}.{1}", i.Instrument, i.Exchange));
            TickWriter.RemoveInstrument(string.Format("{0}.", i.Instrument));
            //TickWriter.RemoveInstrument(string.Format("{0}", i.Instrument));
        }

        /// <summary>
        /// 订阅
        /// </summary>
        /// <param name="list"></param>
        public int Subscribe(IEnumerable<InstrumentInfo> list)
        {
            int x = 0;
            foreach (var i in list)
            {
                // 不包含，没有被排除，需要订阅
                AddInstrument(i);

                bool bSubscribe = false;

                foreach (var api in XApiList)
                {
                    if (api.SubscribedInstrumentsCount < api.MaxSubscribedInstrumentsCount)
                    {
                        api.Subscribe(i.Instrument, i.Exchange);
                        api.Log.Debug("尝试订阅:{0}.{1}", i.Instrument, i.Exchange);
                        bSubscribe = true;
                        break;
                    }
                }

                if(!bSubscribe)
                {
                    Log.Info("超过每个连接数可订数量，{0}.{1}", i.Instrument, i.Exchange);
                    ++x;
                }
            }
            return x;
        }

        /// <summary>
        /// 取消订阅
        /// </summary>
        /// <param name="list"></param>
        public void Unsubscribe(IEnumerable<InstrumentInfo> list)
        {
            foreach(var i in list)
            {
                RemoveInstrument(i);

                foreach (var api in XApiList)
                {
                    if (api.SubscribedInstrumentsContains(i.Instrument, i.Exchange))
                    {
                        api.Unsubscribe(i.Instrument, i.Exchange);
                        api.Log.Debug("取消订阅:{0}.{1}", i.Instrument, i.Exchange);
                    }
                }
            }
        }

        private InstrumentFilterConfig Match(string symbol, List<InstrumentFilterConfig> list)
        {
            foreach(var l in list)
            {
                Regex regex = new Regex(l.SymbolRegex);
                if (regex.Match(symbol).Success)
                {
                    return l;
                }
            }
            return null;
        }

        private void OnRtnDepthMarketData(object sender, ref DepthMarketDataNClass marketData)
        {
            Input.Post(marketData);
        }

        public void OnInputMarketData(DepthMarketDataNClass pDepthMarketData)
        {
            TickWriter.Write(ref pDepthMarketData);
        }

        protected override void OnConnectionStatus(object sender, ConnectionStatus status, ref RspUserLoginField userLogin, int size1)
        {
            base.OnConnectionStatus(sender, status, ref userLogin, size1);
            if (status == ConnectionStatus.Logined)
            {
                TradingDay = userLogin.TradingDay;
                SaveAsTradingDay();
            }
        }
    }
}
