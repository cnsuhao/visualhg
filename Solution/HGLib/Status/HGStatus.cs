﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Windows.Forms;
using System.Threading;
using System.Timers;

namespace HGLib
{
    // ---------------------------------------------------------------------------
    // source control file status enum
    // ---------------------------------------------------------------------------
    public enum SourceControlStatus
    {
        scsUncontrolled = 0,
//        scsCheckedIn,
//        scsCheckedOut,
        scsControlled,
        scsModified,
        scsAdded,
        scsRemoved,
        scsRenamed,
        scsIgnored,
    };

    // ---------------------------------------------------------------------------
    // A delegate type for getting statu changed notifications
    // ---------------------------------------------------------------------------
    public delegate void HGStatusChangedEvent();
    
    // ---------------------------------------------------------------------------
    // HG file status cache. The states of the included files are stored in a
    // file status dictionary. Change events are availabe by HGStatusChanged delegate.
    // ---------------------------------------------------------------------------
    public class HGStatus
    {
        // ------------------------------------------------------------------------
        // status changed event 
        // ------------------------------------------------------------------------
        public event HGStatusChangedEvent HGStatusChanged;

        // file status cache
        HGFileStatusInfoDictionary _fileStatusDictionary = new HGFileStatusInfoDictionary();
        
        // directory watcher map - one for each main directory
        DirectoryWatcherMap _directoryWatcherMap = new DirectoryWatcherMap();

        // HG repo root directories - also SubRepo dirs
        Dictionary<string,bool> _rootDirMap = new Dictionary<string, bool>();

        // trigger thread to observe and assimilate the directory watcher changed file dictionaries
        System.Timers.Timer _timerDirectoryStatusChecker;

        // build process is active
        volatile bool _IsSolutionBuilding = false;
        
        // synchronize to WindowsForms context
        SynchronizationContext _context;

        // Flag to avoid to much rebuild action when .HG\dirstate was changed.
        // Extenal changes of the dirstate file results definitely in cache rebuild
        // action, changes caused by ourself should not. To differ an external
        // from a local change we track a timstamp for our repo modifications.
        DateTime _LocalModifiedTimeStamp = DateTime.Today;

        // max allowed time diff between _LocalModifiedTimeStamp and dirstate changed timestamp.
        // _bRebuildStatusCacheRequred will be set to true when exceeding
        volatile int _MaxDiffToDirstateChangeMS = 3000;

        // wait dead time for cache rebild action. this time must be gone without ocuring any
        // file changed events before rebuild status will start. _bRebuildStatusCacheRequred
        // flag is set to false then.
        volatile int _TimeIntervalForRebuildStatusCacheMS = 1000;

        // status cache is dirty and must be complete reloaded
        volatile bool _bRebuildStatusCacheRequred = false;

        // ------------------------------------------------------------------------
        // init objects and activate the watcher
        // ------------------------------------------------------------------------
        public HGStatus()
        {
            StartDirectoryStatusChecker();
        }

        // ------------------------------------------------------------------------
        // avoid a status requery after hg.dirstate was changed by
        // using this timestamp
        // ------------------------------------------------------------------------
        public void SetDirstateModifiedTimeStamp()
        {
            _LocalModifiedTimeStamp = DateTime.Now;            
        }
        // ------------------------------------------------------------------------
        // toggle directory watching on / off
        // ------------------------------------------------------------------------
        public void EnableRaisingEvents(bool enable)
        {
            _directoryWatcherMap.EnableRaisingEvents(enable);
        }
        
        // ------------------------------------------------------------------------
        // GetFileStatus info for the given filename
        // ------------------------------------------------------------------------
        public bool GetFileStatusInfo(string fileName, out HGFileStatusInfo info)
        {
            return _fileStatusDictionary.TryGetValue(fileName, out info);
        }
    
        // ------------------------------------------------------------------------
        // GetFileStatus for the given filename
        // ------------------------------------------------------------------------
        public SourceControlStatus GetFileStatus(string fileName)
        {
            if (_context == null)
                _context = WindowsFormsSynchronizationContext.Current;

            SourceControlStatus status = SourceControlStatus.scsUncontrolled;
            bool found = false;
            HGFileStatusInfo value;

            lock (_fileStatusDictionary)
            {
                found = _fileStatusDictionary.TryGetValue(fileName, out value);
            }

            if (found)
            {
                switch (value.state)
                {
                    case 'C': status = SourceControlStatus.scsControlled; break;
                    case 'M': status = SourceControlStatus.scsModified; break;
                    case 'A': status = SourceControlStatus.scsAdded; break;
                    case 'R': status = SourceControlStatus.scsRemoved; break;
                    case 'I': status = SourceControlStatus.scsIgnored; break;
                    case 'N': status = SourceControlStatus.scsRenamed; break;
                }
            }

            return status;
        }

        // ------------------------------------------------------------------------
        // fire status changed event
        // ------------------------------------------------------------------------
        void FireStatusChanged(SynchronizationContext context)
        {
            if (HGStatusChanged != null)
            {
                if (context != null)
                {
                    context.Post(new SendOrPostCallback( x =>
                    {
                        HGStatusChanged();
                    }), null);
                }
                else
                {
                    HGStatusChanged();
                }
            }
        }

        // ------------------------------------------------------------------------
        // async query callback
        // ------------------------------------------------------------------------
        void QueryRootStatusCallBack(string rootDirectory, Dictionary<string, char> fileStatusDictionary, SynchronizationContext context)
        {
            lock (_fileStatusDictionary)
            {
                _fileStatusDictionary.Add(fileStatusDictionary);
            }

            // notify calling thread
            FireStatusChanged(context);
        }

        // ------------------------------------------------------------------------
        // async query callback
        // ------------------------------------------------------------------------
        void HandleFileStatusProc(string rootDirectory, Dictionary<string, char> fileStatusDictionary, SynchronizationContext context)
        {
            lock (_fileStatusDictionary)
            {
                _fileStatusDictionary.Add(fileStatusDictionary);
            }
            
            // notify calling thread
            FireStatusChanged(context);
        }

        public bool AnyItemsUnderSourceControl()
        {
            return (_directoryWatcherMap.Count > 0);
        }

        // ------------------------------------------------------------------------
        // SetCacheDirty triggers a RebuildStatusCache event
        // ------------------------------------------------------------------------
        public void SetCacheDirty()
        {
            _bRebuildStatusCacheRequred = true;
        }

        // ------------------------------------------------------------------------
        // adds the given project/directory if not exsist. The status of the files
        // contining in the directory will be scanned by a QueryRootStatus call.
        // ------------------------------------------------------------------------
        public bool UpdateProject(string project, string projectDirectory)
        {
            return AddRootDirectory(projectDirectory);
        }

        // ------------------------------------------------------------------------
        // adds the given projects/directories if not exsist. The status of the files
        // contining in the directory will be scanned by a QueryRootStatus call.
        // ------------------------------------------------------------------------
        public void UpdateProjects(Dictionary<string, string> projects)
        {
            foreach (var kp in projects)
            {
                if (!String.IsNullOrEmpty(kp.Key) && !String.IsNullOrEmpty(kp.Value))
                {
                    UpdateProject(kp.Key, kp.Value);
                }
            }
        }

        // ------------------------------------------------------------------------
        /// Add a root directory and query the status of the contining files 
        /// by a QueryRootStatus call.
        // ------------------------------------------------------------------------
        public bool AddRootDirectory(string directory)
        {
            if (directory == string.Empty)
                return false;

            string root = HG.FindRootDirectory(directory);
            if (root != string.Empty && !_rootDirMap.ContainsKey(root))
            {
                _rootDirMap[root] = true;

                if (!_directoryWatcherMap.ContainsDirectory(root))
                {
                    _directoryWatcherMap.WatchDirectory(root);
                }
                
                HGLib.HG.QueryRootStatus(root, HandleFileStatusProc);
            }
            
            return true;
        }

        #region dirstatus changes
        // ------------------------------------------------------------------------
        /// add file to the repositiry
        // ------------------------------------------------------------------------
        public void AddFiles(string[] fileList)
        {
            _LocalModifiedTimeStamp = DateTime.Now; // avoid a status requery for the repo after hg.dirstate was changed
            HG.AddFiles(fileList, HandleFileStatusProc);
        }

        // ------------------------------------------------------------------------
        /// add file to the repositiry if they are not on the ignore list
        // ------------------------------------------------------------------------
        public void AddNotIgnoredFiles(string[] fileList)
        {
            _LocalModifiedTimeStamp = DateTime.Now; // avoid a status requery for the repo after hg.dirstate was changed
            HG.AddFilesNotIgnored(fileList, HandleFileStatusProc);
        }

        // ------------------------------------------------------------------------
        /// file was renamed , now update hg repository
        // ------------------------------------------------------------------------
        public void PropagateFileRenamed(string[] oldFileNames, string[] newFileNames)
        {
            var oNameList = new List<string> ();
            var nNameList = new List<string>();

            lock (_fileStatusDictionary)
            {
                for (int pos = 0; pos < oldFileNames.Length; ++pos)
                {
                    string oFileName = oldFileNames[pos];
                    string nFileName = newFileNames[pos];

                    if (nFileName.EndsWith("\\"))
                    {
                        // this is an dictionary - skip it
                    }
                    else if (oFileName.ToLower() != nFileName.ToLower())
                    {
                        oNameList.Add(oFileName);
                        nNameList.Add(nFileName);
                        _fileStatusDictionary.Remove(oFileName);
                        _fileStatusDictionary.Remove(nFileName);
                    }
                }
            }

            _LocalModifiedTimeStamp = DateTime.Now; // avoid a status requery for the repo after hg.dirstate was changed
            if (oNameList.Count>0)
                HG.PropagateFileRenamed(oNameList.ToArray(), nNameList.ToArray(), HandleFileStatusProc);
        }

        // ------------------------------------------------------------------------
        // remove given file from cache
        // ------------------------------------------------------------------------
        public void RemoveFileFromCache(string file)
        {
            lock (_fileStatusDictionary)
            {
                _fileStatusDictionary.Remove(file);
            }
        }

        // ------------------------------------------------------------------------
        // file was removed - now update the hg repository
        // ------------------------------------------------------------------------
        public void PropagateFilesRemoved(string[] fileList)
        {
            lock (_fileStatusDictionary)
            {
                foreach (var file in fileList)
                {
                    _fileStatusDictionary.Remove(file);
                }
            }

            _LocalModifiedTimeStamp = DateTime.Now; // avoid a status requery for the repo after hg.dirstate was changed
            HG.PropagateFileRemoved(fileList, HandleFileStatusProc);
        }

        #endregion dirstatus changes

        // ------------------------------------------------------------------------
        // clear the complete cache data
        // ------------------------------------------------------------------------
        public void ClearStatusCache()
        {
            lock (_directoryWatcherMap)
            {
                _directoryWatcherMap.UnsubscribeEvents();
                _directoryWatcherMap.Clear();
                
            }
            
            lock(_rootDirMap)
            {
                _rootDirMap.Clear();
            }

            lock (_fileStatusDictionary)
            {
                _fileStatusDictionary.Clear();
            }
        }

        // ------------------------------------------------------------------------
        // rebuild the entire _fileStatusDictionary map
        // this includes all files in all watched directories
        // ------------------------------------------------------------------------
        void RebuildStatusCache()
        {
            // remove all status entries
            _fileStatusDictionary.Clear();

            _bRebuildStatusCacheRequred = false;
            _LocalModifiedTimeStamp = DateTime.Now;
            
            HGFileStatusInfoDictionary newFileStatusDictionary = new HGFileStatusInfoDictionary();
            foreach (var directoryWatcher in _directoryWatcherMap.WatcherList)
            {
                // reset the watcher map
                directoryWatcher.Value.PopDirtyFilesMap();
            }

            List<string> rootDirList = null; 
            lock (_rootDirMap)
            {
                rootDirList = new List<string>(_rootDirMap.Keys);
            }

            // sort dirs by lenght to query from root top to down root
            rootDirList.Sort((a, b) => a.Length>b.Length?1:-1);
            foreach (string rootDirectory in rootDirList)
            {
                if (rootDirectory != string.Empty)
                {
                    Dictionary<string, char> fileStatusDictionary;
                    if (HG.QueryRootStatus(rootDirectory, out fileStatusDictionary))
                    {
                        Trace.WriteLine("RebuildStatusCache - number of files: " + fileStatusDictionary.Count.ToString());
                        newFileStatusDictionary.Add(fileStatusDictionary);
                    }
                }
            }

            lock (_fileStatusDictionary)
            {
                _fileStatusDictionary = newFileStatusDictionary;
            }
        }

        // ------------------------------------------------------------------------
        // directory watching
        // ------------------------------------------------------------------------
        #region directory watcher

        // ------------------------------------------------------------------------
        // start the trigger thread
        // ------------------------------------------------------------------------
        void StartDirectoryStatusChecker()
        {
            _timerDirectoryStatusChecker = new System.Timers.Timer();
            _timerDirectoryStatusChecker.Elapsed += new ElapsedEventHandler(DirectoryStatusCheckerProc);
            _timerDirectoryStatusChecker.AutoReset = false;
            _timerDirectoryStatusChecker.Interval = 300;
            _timerDirectoryStatusChecker.Enabled  = true;
        }

        public void UpdateSolution_StartUpdate()
        {
            _IsSolutionBuilding = true;
            _timerDirectoryStatusChecker.Enabled = false;
        }

        public void UpdateSolution_Done()
        {
            _IsSolutionBuilding = false;
            _timerDirectoryStatusChecker.Enabled = true;
        }
            
        // ------------------------------------------------------------------------
        // async proc to assimilate the directory watcher state dictionaries
        // ------------------------------------------------------------------------
        void DirectoryStatusCheckerProc(object source, ElapsedEventArgs e)
        {
            if (!_IsSolutionBuilding)
            {
                long numberOfControlledFiles = 0;
                lock (_fileStatusDictionary)
                {
                    numberOfControlledFiles = System.Math.Max(1, _fileStatusDictionary.Count);
                }

                long numberOfChangedFiles = 0;
                double elapsedMS = 0;
                lock (_directoryWatcherMap)
                {
                    numberOfChangedFiles = _directoryWatcherMap.GetNumberOfChangedFiles();
                    TimeSpan timeSpan = new TimeSpan(DateTime.Now.Ticks - _directoryWatcherMap.GetLatestChange().Ticks);
                    elapsedMS = timeSpan.TotalMilliseconds;
                }

                // ui update required flag - only if there were some changes detected
                long dirtyPercent = 100 * numberOfChangedFiles / numberOfControlledFiles;
                //dirtyPercent > 20 ||
                if (_bRebuildStatusCacheRequred || numberOfChangedFiles > 200)
                {
                    // time interval for a full update is 1000 ms
                    if (elapsedMS > _TimeIntervalForRebuildStatusCacheMS)
                    {
                        Trace.WriteLine("DoFullStatusUpdate (NumberOfChangedFiles: " + numberOfChangedFiles.ToString() + " )");
                        RebuildStatusCache();
                        // notify ui thread about the changes
                        FireStatusChanged(_context);
                    }
                }
                else if (numberOfChangedFiles > 0)
                {
                    // dead time interval for file updates are 100 ms
                    if (elapsedMS > 100)
                    {
                        Trace.WriteLine("UpdateDirtyFilesStatus (NumberOfChangedFiles: " + numberOfChangedFiles.ToString() + " )");
                        var fileList = PopDirtyWatcherFiles();
                        if( UpdateFileStatusDictionary(fileList) )
                        {
                            // notify ui thread about the changes
                            FireStatusChanged(_context);
                        }
                    }
                }

            }

            if (!_IsSolutionBuilding)
                _timerDirectoryStatusChecker.Enabled = true;
        }

        // ------------------------------------------------------------------------
        // Check if the watched file is the hg/dirstate and set _bRebuildStatusCacheRequred to true if required
        // Check if the file state must be refreshed
        // Return: true if the file is dirty, false if not
        // ------------------------------------------------------------------------
        bool PrepareWatchedFile(string fileName)
        {
            bool isDirty = true;

            if (DirectoryWatcher.DirectoryExists(fileName))
            {
                // directories are not controlled
                isDirty = false;
            }
            else if (fileName.IndexOf(".hg\\dirstate") > -1)
            {
                TimeSpan elapsed = new TimeSpan(DateTime.Now.Ticks - _LocalModifiedTimeStamp.Ticks);
                Trace.WriteLine("dirstate changed " + elapsed.ToString());
                if (_MaxDiffToDirstateChangeMS < elapsed.TotalMilliseconds)
                {
                    _bRebuildStatusCacheRequred = true;
                    Trace.WriteLine("   ... rebuild of status cache required");
                }
                isDirty = false;
            }
            else if (fileName.IndexOf("\\.hg") != -1)
            {
                // all other .hg files are ignored
                isDirty = false;
            }
            else
            {
                HGFileStatusInfo hgFileStatusInfo;
                
                lock (_fileStatusDictionary)
                {
                    _fileStatusDictionary.TryGetValue(fileName, out hgFileStatusInfo);
                }

                if (hgFileStatusInfo != null)
                {
                    FileInfo fileInfo = new FileInfo(fileName);
                    if (fileInfo.Exists)
                    {
                        // see if the file states are equal
                        if ((hgFileStatusInfo.timeStamp == fileInfo.LastWriteTime &&
                             hgFileStatusInfo.size == fileInfo.Length))
                        {
                            isDirty = false;
                        }
                    }
                    else
                    {
                        if (hgFileStatusInfo.state == 'R' || hgFileStatusInfo.state == '?')
                        {
                            isDirty = false;
                        }
                    }
                }
            }
            return isDirty;
        }

        /// <summary>
        /// list all modified files of all watchers into the return list and reset 
        /// watcher files maps
        /// </summary>
        /// <returns></returns>
        private List<string> PopDirtyWatcherFiles()
        {
            var fileList = new List<string>(); 
            foreach (var directoryWatcher in _directoryWatcherMap.WatcherList)
            {
                var dirtyFilesMap = directoryWatcher.Value.PopDirtyFilesMap();
                if (dirtyFilesMap.Count > 0)
                {
                    // first collect dirty files list
                    foreach (var dirtyFile in dirtyFilesMap)
                    {
                        if (PrepareWatchedFile(dirtyFile.Key) && !_bRebuildStatusCacheRequred)
                        {
                            fileList.Add(dirtyFile.Key);
                        }

                        // could be set by PrepareWatchedFile
                        if (_bRebuildStatusCacheRequred)
                            break;
                    }
                }
            }
            return fileList;
        }

        // ------------------------------------------------------------------------
        // update file status of the watched dirty files
        // ------------------------------------------------------------------------
        bool UpdateFileStatusDictionary(List<string> fileList)
        {
            bool updateUI = false;

            if (_bRebuildStatusCacheRequred)
                return false;

            // now we will get HG status information for the remaining files
            if (!_bRebuildStatusCacheRequred && fileList.Count > 0)
            {
                Dictionary<string, char> fileStatusDictionary;
                SetDirstateModifiedTimeStamp(); 
                if (HG.QueryFileStatus(fileList.ToArray(), out fileStatusDictionary))
                {
                    Trace.WriteLine("got status for watched files - count: " + fileStatusDictionary.Count.ToString());
                    lock (_fileStatusDictionary)
                    {
                        _fileStatusDictionary.Add(fileStatusDictionary);
                    }
                }

                updateUI = true;
            }
            return updateUI;
        }

        #endregion directory watcher
    }
}
