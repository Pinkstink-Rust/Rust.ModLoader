﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Rust.ModLoader
{
    public class ScriptManager : IDisposable
    {
        private const string ScriptExtension = ".cs";
        private const string ScriptFilter = "*" + ScriptExtension;
        private const double UpdateFrequency = 1.0 / 1; // per sec
        private const double ChangeCooldown = 1; // seconds
        private static readonly char[] NameTrimChars = { '_' };

        private readonly object _sync;
        private readonly string _sourcePath;
        private readonly Dictionary<string, Script> _scripts;
        private readonly FileSystemWatcher _watcher;
        private readonly HashSet<RefreshItem> _pendingRefresh;
        private readonly Stopwatch _timeSinceChange;
        private readonly Stopwatch _timeSinceUpdate;

        /// <summary>
        /// Called after instantiating the script but before Initialize is called. Use this to set up the instance with auto-populated field values.
        /// </summary>
        public event Action<IScriptReference> OnScriptLoading;

        /// <summary>
        /// Called after Initialize finishes for a script.
        /// </summary>
        public event Action<IScriptReference> OnScriptLoaded;

        /// <summary>
        /// Called before Dispose is called for a script.
        /// </summary>
        public event Action<IScriptReference> OnScriptUnloading;

        /// <summary>
        /// Called after Dispose finishes for a script.
        /// </summary>
        public event Action<IScriptReference> OnScriptUnloaded;

        public ScriptManager(string sourcePath)
        {
            _sync = new object();
            _sourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
            _scripts = new Dictionary<string, Script>(StringComparer.OrdinalIgnoreCase);
            _pendingRefresh = new HashSet<RefreshItem>();
            _timeSinceChange = Stopwatch.StartNew();
            _timeSinceUpdate = Stopwatch.StartNew();

            RefreshAll();

            _watcher = new FileSystemWatcher(sourcePath, ScriptFilter)
            {
                InternalBufferSize = 32 * 1024,
                EnableRaisingEvents = true,
            };

            _watcher.Created += (sender, args) => Refresh(args.FullPath);
            _watcher.Deleted += (sender, args) => Refresh(args.FullPath);
            _watcher.Changed += (sender, args) => Refresh(args.FullPath);
            _watcher.Renamed += (sender, args) =>
            {
                Refresh(args.FullPath);
                Refresh(args.OldFullPath);
            };
        }

        public void Dispose()
        {
            _watcher?.Dispose();
        }

        internal void Update()
        {
            if (_timeSinceUpdate.Elapsed.TotalSeconds < UpdateFrequency)
            {
                return;
            }

            _timeSinceUpdate.Restart();

            lock (_sync)
            {
                if (_timeSinceChange.Elapsed.TotalSeconds < ChangeCooldown)
                {
                    return;
                }

                if (_pendingRefresh.Count == 0)
                {
                    return;
                }

                // TODO: this will be a lot more involved once it supports dependencies between scripts
                foreach (var item in _pendingRefresh)
                {
                    if (!File.Exists(item.Path))
                    {
                        if (_scripts.TryGetValue(item.Name, out var script))
                        {
                            script.Dispose();
                            _scripts.Remove(item.Name);
                        }
                    }
                    else if (_scripts.TryGetValue(item.Name, out var script))
                    {
                        UpdateScript(script, item.Path);
                    }
                    else
                    {
                        var newScript = new Script(this, item.Name);
                        _scripts.Add(item.Name, newScript);

                        UpdateScript(newScript, item.Path);
                    }
                }

                _pendingRefresh.Clear();
            }
        }

        private void UpdateScript(Script script, string path)
        {
            try
            {
                script.Update(path);
            }
            catch (Exception e)
            {
                script.ReportError("Update", e);
            }
        }

        private void Refresh(string scriptPath)
        {
            var name = Path.GetFileNameWithoutExtension(scriptPath);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            lock (_sync)
            {
                _pendingRefresh.Add(new RefreshItem(name, scriptPath));
                _timeSinceChange.Restart();
            }
        }

        private void RefreshAll()
        {
            foreach (var scriptPath in Directory.EnumerateFiles(_sourcePath, ScriptFilter))
            {
                Refresh(scriptPath);
            }
        }

        internal void ScriptLoading(IScriptReference script)
        {
            OnScriptLoading?.Invoke(script);
        }

        internal void ScriptLoaded(IScriptReference script)
        {
            OnScriptLoaded?.Invoke(script);
            Broadcast("OnScriptLoaded", script);
        }

        internal void ScriptUnloading(IScriptReference script)
        {
            OnScriptUnloading?.Invoke(script);
        }

        internal void ScriptUnloaded(IScriptReference script)
        {
            OnScriptUnloaded?.Invoke(script);
            Broadcast("OnScriptUnloaded", script);
        }

        internal IEnumerable<string> PopulateScriptReferences(RustScript rustScript)
        {
            var type = rustScript.GetType();
            
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                if (field.FieldType != typeof(IScriptReference) || field.IsSpecialName)
                {
                    continue;
                }

                var scriptName = field.Name.Trim(NameTrimChars);
                if (!_scripts.TryGetValue(scriptName, out var script))
                {
                    continue;
                }

                field.SetValue(rustScript, script);
                yield return script.Name;
            }

            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var property in properties)
            {
                if (property.PropertyType != typeof(IScriptReference) || !property.CanWrite)
                {
                    continue;
                }
                
                var scriptName = property.Name.Trim(NameTrimChars);
                if (!_scripts.TryGetValue(scriptName, out var script))
                {
                    continue;
                }

                property.SetValue(rustScript, script);
                yield return script.Name;
            }
        }

        public void Broadcast(string methodName)
        {
            lock (_sync)
            {
                foreach (var script in _scripts.Values)
                {
                    script.InvokeProcedure(methodName);
                }
            }
        }

        public void Broadcast<T0>(string methodName, T0 arg0)
        {
            lock (_sync)
            {
                foreach (var script in _scripts.Values)
                {
                    script.InvokeProcedure(methodName, arg0);
                }
            }
        }

        public void Broadcast<T0, T1>(string methodName, T0 arg0, T1 arg1)
        {
            lock (_sync)
            {
                foreach (var script in _scripts.Values)
                {
                   script.InvokeProcedure(methodName, arg0, arg1);
                }
            }
        }
    }
}
