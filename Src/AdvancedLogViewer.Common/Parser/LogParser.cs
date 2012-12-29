﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;

namespace AdvancedLogViewer.Common.Parser
{
    public class LoadingCompleteEventArgs : EventArgs
    {
        public LoadingCompleteEventArgs(bool logIsChanged)
        {
            this.LogIsChanged = logIsChanged;
        }

        public bool LogIsChanged { get; private set; }
    }


    public class LogParser
    {
        private static Scarfsail.Logging.Log log = new Scarfsail.Logging.Log();

        private long prevFileSize;
        private int prevLastLine = -1;
        private Thread loadLogEntries;
        private LogPattern logPattern;
        private bool cancel;

        public delegate void LoadingCompleteEventHandler(object sender, LoadingCompleteEventArgs e);

        public LogParser(string logFileName) : this(logFileName, null)
        {
            
        }

        public LogParser(string logFileName, LogPattern logPattern)
        {
            if (!File.Exists(logFileName))
                throw new FileNotFoundException(logFileName);

            this.LogFileName = logFileName;
            this.LogEntries = new List<LogEntry>();
            this.LogEntriesLocker = new object();

            this.AllLogPartsFileNames = new List<string>();
            this.ForcedLogPattern = logPattern != null; 
            this.logPattern = logPattern ?? PatternManager.GetPatternForLog(logFileName);
            
            
            log.DebugFormat("LogParsers created for file: {0} , Pattern: {1}", logFileName, this.logPattern);
        }

        public Thread LoadLogEntriesAsync()
        {
            if (this.LoadingInProgress)
                return null;
            log.Debug("LoadLogEntriesAsync()");

            this.loadLogEntries = new Thread(new ThreadStart(this.LoadLogEntries));
            this.loadLogEntries.Start();

            return this.loadLogEntries;
        }

        public void LoadLogEntries()
        {
            if (LoadingInProgress)
                return;
            log.Debug("LoadLogEntries()");

            bool logIsChanged = false;
            try
            {
                Monitor.Enter(this.LogEntriesLocker);

                this.LoadingInProgress = true;
                
                this.PopulateOtherLogFileNameParts();

                int lineNumber = 0;
                if (!File.Exists(this.LogFileName))
                {
                    log.Warn("Log file doesn't exists.");
                    this.LogEntries.Clear();
                    lineNumber = 0;
                    this.LogFileExists = false;
                }
                else
                {
                    this.LogFileExists = true;
                    using (FileStream fs = File.Open(this.LogFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        using (TextReader sr = new StreamReader(fs))
                        {
                            LogEntry tmpLogEntry = new LogEntry();
                            LogEntry currentLogEntry = null;
                            LogEntry logEntryFromPrevLoad = null;

                            StringBuilder messageBuilder = new StringBuilder(524288); //0.5 MB

                            long fileSize = fs.Length;
                            int readFromLine = 1;
                            DateTime nextProgressReportTime = DateTime.Now.AddMilliseconds(50);
                            int nextProgressReportLine = 200;
                            log.Debug("Text reader opened");
                            if (this.LogEntries.Count > 0)
                            {
                                log.Debug("LogEntries.Count > 0");

                                if (fileSize > prevFileSize)
                                {
                                    log.Debug("Log is updated (fileSize > prevFileSize)");
                                    logEntryFromPrevLoad = this.LogEntries[this.LogEntries.Count - 1];
                                    currentLogEntry = logEntryFromPrevLoad;
                                    readFromLine = prevLastLine + 1;
                                }
                                else if (fileSize < prevFileSize)
                                {
                                    log.Debug("New log file (fileSize < prevFileSize)");
                                    this.LogEntries.Clear();
                                }
                                else
                                {
                                    log.Debug("File is the same as before");
                                    return;
                                }
                            }
                            prevFileSize = fileSize;

                            int itemNumber = this.LogEntries.Count == 0 ? 0 : this.LogEntries[this.LogEntries.Count - 1].ItemNumber + 1;
                            this.cancel = false;

                            string line;
                            log.Debug("Reading lines ...");
                            while ((line = sr.ReadLine()) != null)
                            {
                                if (this.cancel)
                                    break;

                                lineNumber++;
                                if (lineNumber < readFromLine)
                                    continue;

                                if (lineNumber > nextProgressReportLine)
                                    if (DateTime.Now > nextProgressReportTime && lineNumber >= 100)
                                    {
                                        nextProgressReportTime = DateTime.Now.AddMilliseconds(2000);
                                        nextProgressReportLine = lineNumber + 10000;
                                        Monitor.Exit(this.LogEntriesLocker);
                                        this.OnLoadingProgress();
                                        Monitor.Enter(this.LogEntriesLocker);
                                    }
                                    else
                                    {
                                        nextProgressReportLine = lineNumber + 10000;
                                    }


                                int prevPos = 0;
                                int pos = 0;
                                bool patternFits = true;

                                foreach (PatternItem patternItem in this.logPattern.PatternItems)
                                {
                                    pos = patternItem.EndsWith == null ? line.Length : patternItem.EndsWith.Length == 1 ? line.IndexOf(patternItem.EndsWith[0], prevPos) : line.IndexOf(patternItem.EndsWith, prevPos, StringComparison.Ordinal);

                                    if (pos == -1)
                                    {
                                        patternFits = false;
                                        break;
                                    }

                                    string value = line.Substring(prevPos, pos - prevPos);

                                    if (patternItem.DoLTrim)
                                        value = value.TrimStart(new char[] { ' ' });

                                    if (!tmpLogEntry.SaveValue(patternItem.ItemType, value))
                                    {
                                        patternFits = false;
                                        break;
                                    }

                                    if (patternItem.EndsWith != null)
                                    {
                                        prevPos = pos + patternItem.EndsWith.Length;
                                    }

                                    tmpLogEntry.LineInFile = lineNumber;
                                    tmpLogEntry.ItemNumber = itemNumber;
                                }

                                if (patternFits)
                                {
                                    itemNumber++;
                                    //It's new log message
                                    if (currentLogEntry != null && currentLogEntry != logEntryFromPrevLoad)
                                    {
                                        currentLogEntry.SaveValue(PatternItemType.Message, messageBuilder.ToString());
                                        LogEntries.Add(currentLogEntry);
                                    }

                                    messageBuilder.Length = 0;
                                    currentLogEntry = tmpLogEntry;
                                    tmpLogEntry = new LogEntry();
                                }
                                else
                                {
                                    //It's part of inner message, add it to current log entry
                                    if (currentLogEntry != null)
                                        messageBuilder.Append(Environment.NewLine + line);
                                    else
                                    {
                                        itemNumber++;
                                        currentLogEntry = new LogEntry();
                                        foreach (PatternItem patternItem in this.logPattern.PatternItems)
                                        {
                                            if (patternItem.ItemType == PatternItemType.Message)
                                                currentLogEntry.SaveValue(patternItem.ItemType, line);
                                            else
                                                currentLogEntry.SaveValue(patternItem.ItemType, String.Empty);
                                        }

                                    }
                                }
                            }

                            if (currentLogEntry != null && !String.IsNullOrEmpty(currentLogEntry.Message) && currentLogEntry != logEntryFromPrevLoad)
                            {                                
                                currentLogEntry.SaveValue(PatternItemType.Message, messageBuilder.ToString());

                                LogEntries.Add(currentLogEntry);
                            }
                        }
                    }
                }
                //If last line in previous loading is different from current last one, we have change in log file
                if (prevLastLine != lineNumber)
                {
                    prevLastLine = lineNumber;
                    logIsChanged = true;
                }
            }
            finally
            {
                if (logIsChanged)
                {
                    /*
                    Thread parseDateThread = new Thread(new ThreadStart(new MethodInvoker(() => ParseDateTimes(this.OnLoadingError))));
                    parseDateThread.Start();
                    */
                    ParseDateTimes(this.LogEntries, this.OnLoadingError);
                }
                Monitor.Exit(this.LogEntriesLocker);

                //We are finished
                this.OnLoadingComplete(logIsChanged);
                this.LoadingInProgress = false;
            }
        }
                
        public void AbortLoading()
        {
            log.Debug("Abort loading()");
            if (loadLogEntries != null && loadLogEntries.ThreadState == ThreadState.Running)
            {
                this.cancel = true;
            }
            this.loadLogEntries.Join();
        }


        
        public event EventHandler LoadingProgress;

        public event LoadingCompleteEventHandler LoadingComplete;

        public event ErrorEventHandler LoadingError;


        /// <summary>File name of parsed log</summary>
        public string LogFileName { get; private set; }
        
        /// <summary>Contains list of all log parts including base log file name (e.g.: x.log, x.log.1, x.log.2, ....).</summary>
        public List<String> AllLogPartsFileNames { get; private set; }

        /// <summary>It's base log file name - without number suffix.</summary>
        public string BaseLogFileName { get; private set; }

        /// <summary>
        /// List of log entries. Any manipulation with this object has to be in critical section locked by LogEntriesLocker
        /// </summary>
        public List<LogEntry> LogEntries { get; private set; }
        
        /// <summary>
        /// Locker object which needs to be used in order to work with LogEntries object.
        /// </summary>
        public object LogEntriesLocker { get; private set; }

        /// <summary>
        /// Thread safe property which returns count of items in LogEntries collection.
        /// </summary>
        public int LogEntriesCount
        {
            get 
            {
                lock (LogEntriesLocker)
                {
                    return LogEntries.Count;
                }
            }
        }

        public bool LoadingInProgress { get; private set; }
        
        public bool DateIsParsed {get; private set; }

        public LogPattern LogPattern { get { return this.logPattern; } }

        public int LinesCount { get { return this.prevLastLine; } }

        public bool ForcedLogPattern { get; private set; }

        public bool LogFileExists { get; private set; }

        public string GetFormattedMessageDetailHeader(LogEntry logEntry)
        {
            return this.logPattern.GetFormattedDetailHeader(logEntry.DateText, logEntry.Thread, logEntry.Type, logEntry.Class);
        }

        public string GetFormattedWholeEntry(LogEntry logEntry)
        {
            return this.logPattern.GetFormattedWholeEntry(logEntry.DateText, logEntry.Thread, logEntry.Type, logEntry.Class, logEntry.Message);
        }
        
        protected void OnLoadingProgress()
        {
            log.Debug("OnLoadingProgress()");
            if (this.LoadingProgress != null)
                this.LoadingProgress(this, null);
        }

        protected void OnLoadingComplete(bool logIsChanged)
        {
            log.Debug("OnLoadingComplete(logIsChanged:"+logIsChanged.ToString()+")");
            if (this.LoadingComplete != null)
                this.LoadingComplete(this, new LoadingCompleteEventArgs(logIsChanged));
        }
        
        protected void OnLoadingError(string errorMessage)
        {
            log.Debug("OnLoadingError(errorMessage:" + errorMessage + ")");
            if (this.LoadingError != null)
                this.LoadingError(this, new ErrorEventArgs(new Exception(errorMessage)));
        }


        private delegate void LoadingErrorCallBack(string errorMessage);

        private void ParseDateTimes(List<LogEntry> logEntries, LoadingErrorCallBack errorMessageCallBack)
        {
            int errorCount = 0;
            this.DateIsParsed = false;
            log.Debug("ParserDateTimes");
            foreach (LogEntry entry in logEntries)
            {
                if (!entry.ParseDate(this.LogPattern.DateTimeFormat))
                    errorCount++;
                else
                    errorCount = 0;
                if (errorCount >= 5)
                {
                    //errorMessageCallBack(String.Format("Unable to parse date: {0}{2}with provided format: {1}{2}Date functions will be disabled.", entry.DateText, this.logPattern.DateTimeFormat, Environment.NewLine));
                    errorMessageCallBack("Unable to parse date.");
                    return;
                }
            }
            this.DateIsParsed = true;
        }

        public static string GetBaseLogFileName(string logFileName)
        {
            string ext = Path.GetExtension(logFileName);
            int currentExtNumber = -1;
            if (Int32.TryParse(ext.Replace(".", ""), out currentExtNumber))
            {
                return logFileName.Substring(0, logFileName.Length - ext.Length);
            }
            else
            {
                return logFileName;
            }
        }

        private void PopulateOtherLogFileNameParts()
        {
            log.Debug("PopulateOtherLogFileNameParts()");
            //Try to find other parts of this log (.1 .2 ....)
            this.BaseLogFileName = GetBaseLogFileName(this.LogFileName);

            this.AllLogPartsFileNames.Clear();
            if (File.Exists(BaseLogFileName))
                this.AllLogPartsFileNames.Add(BaseLogFileName);

            int i = 1;
            int nonExistingExtensions = 0;
            while (true)
            {
                string fileName = BaseLogFileName + "." + i.ToString();
                if (File.Exists(fileName))
                {
                    this.AllLogPartsFileNames.Add(fileName);
                    nonExistingExtensions = 0;
                }
                else
                {
                    nonExistingExtensions++;
                    if (nonExistingExtensions == 5)
                        break;
                }
                i++;
            }
        }

    }
}