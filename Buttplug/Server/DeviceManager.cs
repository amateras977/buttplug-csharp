﻿// <copyright file="DeviceManager.cs" company="Nonpolynomial Labs LLC">
//     Buttplug C# Source Code File - Visit https://buttplug.io for more info about the project.
//     Copyright (c) Nonpolynomial Labs LLC. All rights reserved. Licensed under the BSD 3-Clause
//     license. See LICENSE file in the project root for full license information.
// </copyright>

using Buttplug.Core;
using Buttplug.Core.Devices;
using Buttplug.Core.Logging;
using Buttplug.Core.Messages;
using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Buttplug.Server
{
    public class DeviceManager
    {
        public event EventHandler<MessageReceivedEventArgs> DeviceMessageReceived;

        public event EventHandler<EventArgs> ScanningFinished;

        private readonly List<IDeviceSubtypeManager> _managers;

        private readonly List<string> _managerSearchDirs;

        [NotNull]
        private readonly IButtplugLog _bpLogger;

        private readonly IButtplugLogManager _bpLogManager;
        private readonly SemaphoreSlim _scanLock;

        private bool _hasAddedSubtypeManagers;
        private bool _isScanning;

        private long _deviceIndexCounter;
        private bool _sentFinished;

        // Needs to be internal for tests.
        // ReSharper disable once MemberCanBePrivate.Global
        internal Dictionary<uint, IButtplugDevice> _devices { get; }

        public uint SpecVersion { get; set; }

        public DeviceManager(IButtplugLogManager aLogManager, List<string> aSearchDirs = null)
        {
            ButtplugUtils.ArgumentNotNull(aLogManager, nameof(aLogManager));
            _bpLogManager = aLogManager;
            _bpLogger = _bpLogManager.GetLogger(GetType());
            _bpLogger.Info("Setting up DeviceManager");
            _sentFinished = true;
            _devices = new Dictionary<uint, IButtplugDevice>();
            _deviceIndexCounter = 0;
            _scanLock = new SemaphoreSlim(1);
            _hasAddedSubtypeManagers = false;
            _isScanning = false;

            _managerSearchDirs = aSearchDirs ?? new List<string> { Directory.GetCurrentDirectory() };

            _managers = new List<IDeviceSubtypeManager>();
        }

        ~DeviceManager()
        {
            RemoveAllDevices();
        }

        private static Dictionary<string, MessageAttributes>
            GetAllowedMessageTypesAsDictionary(IButtplugDevice aDevice, uint aSchemaVersion)
        {
            ButtplugUtils.ArgumentNotNull(aDevice, nameof(aDevice));

            return (from msg in aDevice.AllowedMessageTypes
                    let msgVersion = ButtplugMessage.GetSpecVersion(msg)
                    where msgVersion <= aSchemaVersion
                    select msg)
                .ToDictionary(aMsg => aMsg.Name, aDevice.GetMessageAttrs);
        }

        private void DeviceAddedHandler(object aObj, DeviceAddedEventArgs aEvent)
        {
            // Devices can be turned off by the time they get to this point, at which point they end
            // up null. Make sure the device isn't null.
            if (aEvent.Device == null)
            {
                return;
            }

            var duplicates = from x in _devices
                             where x.Value.Identifier == aEvent.Device.Identifier
                             select x;
            if (duplicates.Any() && (duplicates.Count() > 1 || duplicates.First().Value.IsConnected))
            {
                _bpLogger.Debug($"Already have device {aEvent.Device.Name} in Devices list");
                return;
            }

            // If we get to 4 billion devices connected, this may be a problem.
            var deviceIndex = duplicates.Any() ? duplicates.First().Key : (uint)Interlocked.Increment(ref _deviceIndexCounter);
            _bpLogger.Info((duplicates.Any() ? "Re-" : string.Empty) + $"Adding Device {aEvent.Device.Name} at index {deviceIndex}");

            _devices[deviceIndex] = aEvent.Device;
            _devices[deviceIndex].Index = deviceIndex;
            aEvent.Device.DeviceRemoved += DeviceRemovedHandler;
            aEvent.Device.MessageEmitted += MessageEmittedHandler;
            var msg = new DeviceAdded(
                deviceIndex,
                aEvent.Device.Name,
                GetAllowedMessageTypesAsDictionary(aEvent.Device, SpecVersion));

            DeviceMessageReceived?.Invoke(this, new MessageReceivedEventArgs(msg));
        }

        private void MessageEmittedHandler(object aSender, MessageReceivedEventArgs aEvent)
        {
            DeviceMessageReceived?.Invoke(this, aEvent);
        }

        private void DeviceRemovedHandler(object aObj, EventArgs aEvent)
        {
            if ((aObj as ButtplugDevice) == null)
            {
                _bpLogger.Error("Got DeviceRemoved message from an object that is not a ButtplugDevice.");
                return;
            }

            var device = (ButtplugDevice)aObj;

            // The device itself will fire the remove event, so look it up in the dictionary and
            // translate that for clients.
            var entry = (from x in _devices where x.Value.Identifier == device.Identifier select x).ToList();
            if (!entry.Any())
            {
                _bpLogger.Error("Got DeviceRemoved Event from object that is not in devices dictionary");
            }

            if (entry.Count > 1)
            {
                _bpLogger.Error("Device being removed has multiple entries in device dictionary.");
            }

            foreach (var pair in entry.ToList())
            {
                pair.Value.DeviceRemoved -= DeviceRemovedHandler;
                _bpLogger.Info($"Device removed: {pair.Key} - {pair.Value.Name}");
                DeviceMessageReceived?.Invoke(this, new MessageReceivedEventArgs(new DeviceRemoved(pair.Key)));
            }
        }

        private void ScanningFinishedHandler(object aObj, EventArgs aEvent)
        {
            Task.Run(() =>
            {
                _scanLock.Wait();
                if (_sentFinished)
                {
                    _scanLock.Release();
                    return;
                }

                var done = true;
                _managers.ForEach(aMgr => done &= !aMgr.IsScanning());
                if (!done)
                {
                    _scanLock.Release();
                    return;
                }

                _sentFinished = true;
                ScanningFinished?.Invoke(this, new EventArgs());
                _scanLock.Release();
            });
        }

        public async Task<ButtplugMessage> SendMessageAsync(ButtplugMessage aMsg, CancellationToken aToken = default(CancellationToken))
        {
            ButtplugUtils.ArgumentNotNull(aMsg, nameof(aMsg));
            var id = aMsg.Id;
            switch (aMsg)
            {
                case StartScanning _:
                    _bpLogger.Debug("Got StartScanning Message");
                    try
                    {
                        await StartScanning();
                    }
                    catch (ButtplugDeviceException aEx)
                    {
                        throw new ButtplugDeviceException(_bpLogger, aEx.Message, id);
                    }

                    return new Ok(id);

                case StopScanning _:
                    _bpLogger.Debug("Got StopScanning Message");
                    StopScanning();
                    return new Ok(id);

                case StopAllDevices _:
                    _bpLogger.Debug("Got StopAllDevices Message");
                    var isOk = true;
                    var errorMsg = string.Empty;
                    foreach (var d in _devices.ToList())
                    {
                        if (!d.Value.IsConnected)
                        {
                            continue;
                        }

                        var r = await d.Value.ParseMessageAsync(new StopDeviceCmd(d.Key, aMsg.Id), aToken).ConfigureAwait(false);
                        if (r is Ok)
                        {
                            continue;
                        }

                        isOk = false;
                        errorMsg += $"{(r as Error).ErrorMessage}; ";
                    }

                    if (isOk)
                    {
                        return new Ok(aMsg.Id);
                    }

                    throw new ButtplugDeviceException(_bpLogger, errorMsg, aMsg.Id);
                case RequestDeviceList _:
                    _bpLogger.Debug("Got RequestDeviceList Message");
                    var msgDevices = _devices.Where(aDevice => aDevice.Value.IsConnected)
                        .Select(aDevice => new DeviceMessageInfo(
                            aDevice.Key,
                            aDevice.Value.Name,
                            GetAllowedMessageTypesAsDictionary(aDevice.Value, SpecVersion))).ToList();
                    return new DeviceList(msgDevices.ToArray(), id);

                // If it's a device message, it's most likely not ours.
                case ButtplugDeviceMessage m:
                    _bpLogger.Trace($"Sending {aMsg.GetType().Name} to device index {m.DeviceIndex}");
                    if (_devices.ContainsKey(m.DeviceIndex))
                    {
                        return await _devices[m.DeviceIndex].ParseMessageAsync(m, aToken).ConfigureAwait(false);
                    }

                    throw new ButtplugDeviceException(_bpLogger,
                        $"Dropping message for unknown device index {m.DeviceIndex}", id);
            }

            throw new ButtplugMessageException(_bpLogger, $"Message type {aMsg.GetType().Name} unhandled by this server.", id);
        }

        internal void RemoveAllDevices()
        {
            StopScanning();
            var removeDevices = _devices.Values;
            _devices.Clear();
            foreach (var d in removeDevices)
            {
                d.DeviceRemoved -= DeviceRemovedHandler;
                d.Disconnect();
            }
        }

        private async Task StartScanning()
        {
            _scanLock.Wait();
            if (_isScanning)
            {
                return;
            }

            _isScanning = true;
            if (!_hasAddedSubtypeManagers)
            {
                await AddAllSubtypeManagers();
            }
            if (!_managers.Any())
            {
                throw new ButtplugDeviceException(_bpLogger, "No DeviceSubtypeManagers available to scan with.");
            }
            _scanLock.Release();
            _sentFinished = false;
            _managers.ForEach(aMgr => aMgr.StartScanning());
        }

        /// <summary>
        /// Finds all Manager DLLs (DLLs named "Buttplug.Server.Managers.*.dll") in the local
        /// directory, loads them into the assembly cache, scans them for DeviceSubtypeManager
        /// deriving classes, and adds instances of those classes to the device manager. Handy if you
        /// just want to load all of the device managers at once, versus one-by-one by hand. Note
        /// that this function can be slow enough to delay startup and GUIs, and might best be run on
        /// a thread or Task.
        /// </summary>
        public async Task AddAllSubtypeManagers()
        {
            _bpLogger.Debug("Searching for Subtype Managers");

            // Assume all assemblies will be named as "Buttplug.Server.Managers.*.dll" because that's
            // what we do now anyways.
            foreach (var path in _managerSearchDirs)
            {
                _bpLogger.Debug($"Searching {path} for Subtype Managers");
                foreach (var dll in Directory.GetFiles(path, "Buttplug.Server.Managers.*.dll"))
                {
                    _bpLogger.Debug($"Loading {dll}");
                    Assembly.LoadFile(dll);
                }
            }

            // Within the assemblies, extract all subclasses of DeviceSubtypeManager, which are what
            // we need to add here.
            var types =
                from a in AppDomain.CurrentDomain.GetAssemblies()
                from t in a.GetTypes()
                where t.IsSubclassOf(typeof(DeviceSubtypeManager))
                select t;

            // Add new instances of all found types to the DeviceManager. All DeviceSubtypeManager
            // classes should take a IButtplugLogManager as a constructor parameter. If not (which
            // will at least be the case for BluetoothDeviceManager, which is a base class), log and continue.
            foreach (var t in types)
            {
                try
                {
                    _bpLogger.Info($"Adding subtype manager {t.Name} as part of AddAllSubtypeManagers.");
                    var mgr = (IDeviceSubtypeManager)Activator.CreateInstance(t, new[] { _bpLogManager });
                    AddDeviceSubtypeManager(mgr);
                }
                catch (MissingMethodException)
                {
                    _bpLogger.Info($"Subtype manager {t.Name} not added, missing appropriate constructor.");
                }
            }
        }

        internal void StopScanning()
        {
            _managers.ForEach(aMgr => aMgr.StopScanning());
        }

        public void AddDeviceSubtypeManager<T>([NotNull] Func<IButtplugLogManager, T> aCreateMgrFunc)
            where T : IDeviceSubtypeManager
        {
            AddDeviceSubtypeManager(aCreateMgrFunc(_bpLogManager));
        }

        public void AddDeviceSubtypeManager(IDeviceSubtypeManager aMgr)
        {
            foreach (var mgr in _managers)
            {
                if (mgr.GetType() == aMgr.GetType())
                {
                    _bpLogger.Info($"Subtype Manager of type {aMgr.GetType().Name} already added. Ignoring.");
                    return;
                }
            }

            _bpLogger.Debug($"Adding {aMgr.GetType().Name} subtype manager");
            _managers.Add(aMgr);
            aMgr.DeviceAdded += DeviceAddedHandler;
            aMgr.ScanningFinished += ScanningFinishedHandler;
        }
    }
}