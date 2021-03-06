//
//  IVPN Client Desktop
//  https://github.com/ivpn/desktop-app-ui
//
//  Created by Stelnykovych Alexandr.
//  Copyright (c) 2020 Privatus Limited.
//
//  This file is part of the IVPN Client Desktop.
//
//  The IVPN Client Desktop is free software: you can redistribute it and/or
//  modify it under the terms of the GNU General Public License as published by the Free
//  Software Foundation, either version 3 of the License, or (at your option) any later version.
//
//  The IVPN Client Desktop is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
//  or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more
//  details.
//
//  You should have received a copy of the GNU General Public License
//  along with the IVPN Client Desktop. If not, see <https://www.gnu.org/licenses/>.
//

﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using IVPN.Models.Configuration;
using IVPN.VpnProtocols;
using IVPNCommon.Interfaces;

namespace IVPN.Models
{
    public class Servers : ModelBase, IServers
    {
        private VpnServersInfo __AllVpnServers;
        private List<ServerLocation> __ServersList;

        public event OnPingUpdatedDelegate OnPingsUpdated = delegate { };
        public event OnFasterServerDetectedDelegate OnFasterServerDetected = delegate {};

        public event OnPingUpdateRequestDelegate OnPingUpdateRequired = delegate { };

        // List of servers of current VPN protocol
        public List<ServerLocation> ServersList
        {
            get => __ServersList;
            private set
            {
                DoPropertyWillChange();
                __ServersList = value;
                DoPropertyChanged();
            }
        }
        
        // minimum time between ping-check
        private const int MinPingCheckIntervalMs = 1000 * 10; // 10 seconds
        private const int DefaultPingTimeoutMs = 3000;
        private const int DefaultPingRetriesCount = 3;
               
        private readonly AppSettings __AppSettings;
        private DateTime __LastPingTimeReceived;
        
        public Servers(AppSettings appSettings)
        {
            __AppSettings = appSettings;
            ServersList = new List<ServerLocation>();

            __AppSettings.PropertyChanged += delegate(object sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName.Equals(nameof(__AppSettings.VpnProtocolType)))
                    UpdateServers(__AllVpnServers);
            };
        }

        private void GetPingParams(out int pingTimeoutMs, out int pingRetriesCount)
        {
            pingTimeoutMs = DefaultPingTimeoutMs;
            pingRetriesCount = DefaultPingRetriesCount;

            if (__LastPingTimeReceived == default(DateTime))
            { 
                // if it is a first ping request - increase pinbg parameters to get better result quality
                // (pinging immediately application start can lead to high ping time, because of CPU load)
                pingTimeoutMs *= 2;
                pingRetriesCount *= 2;
            }
        }

        private void RequestPings()
        {
            GetPingParams(out var pingTimeoutMs, out var pingRetriesCount);
            OnPingUpdateRequired(pingTimeoutMs, pingRetriesCount);
        }

        public void UpdateServers(VpnServersInfo allVpnServers)
        {
            if (allVpnServers == null || !allVpnServers.IsAnyServers())
                return;

            allVpnServers.OpenVPNServers.Sort((x, y) =>
            {
                int ret = string.Compare(x.CountryCode, y.CountryCode, StringComparison.Ordinal);
                if (ret == 0)
                    ret = string.Compare(x.City, y.City, StringComparison.Ordinal);
                return ret;
            });

            allVpnServers.WireGuardServers.Sort((x, y) =>
            {
                int ret = string.Compare(x.CountryCode, y.CountryCode, StringComparison.Ordinal);
                if (ret == 0)
                    ret = string.Compare(x.City, y.City, StringComparison.Ordinal);
                return ret;
            });

            var oldServersList = ServersList;
            var serversList = new List<ServerLocation>();
            if (__AppSettings.VpnProtocolType == VpnType.OpenVPN)
            {
                foreach (var server in allVpnServers.OpenVPNServers)
                {
                    if (server == null)
                        continue;

                    var newServer = new ServerLocation(server);
                    serversList.Add(newServer);

                    var oldServer = oldServersList.FirstOrDefault(s => s.VpnServer.GatewayId == server.GatewayId);

                    if (oldServer != null)
                    {
                        newServer.PingTime = oldServer.PingTime;
                        newServer.PingTimeRelative = oldServer.PingTimeRelative;
                        newServer.IsSelected = oldServer.IsSelected;
                    }
                }
            }
            else if (__AppSettings.VpnProtocolType == VpnType.WireGuard)
            {
                foreach (var server in allVpnServers.WireGuardServers)
                {
                    if (server == null)
                        continue;

                    var newServer = new ServerLocation(server);
                    serversList.Add(newServer);

                    var oldServer = oldServersList.FirstOrDefault(s => s.VpnServer.GatewayId == server.GatewayId);

                    if (oldServer != null)
                    {
                        newServer.PingTime = oldServer.PingTime;
                        newServer.PingTimeRelative = oldServer.PingTimeRelative;
                        newServer.IsSelected = oldServer.IsSelected;
                    }
                }
            }

            __AllVpnServers = allVpnServers;
            ServersList = serversList;

            RequestPings();
        }

        public void UpdateServersPings(Dictionary<string, int> serversPings)
        {
            if (serversPings == null || !serversPings.Any())
                return;

            List<ServerLocation> servers = new List<ServerLocation>(__ServersList);

            foreach (var pair in serversPings)
            {
                foreach(var svr in servers)
                {
                    if (svr.VpnServer.IsContainHostIpAddress(pair.Key))
                        svr.PingTime = pair.Value;
                }
            }

            __LastPingTimeReceived = DateTime.Now;

            DoFindFastestServer();

            // Update relative ping time (in use by UI to show green/orange/red dot)
            RecalculateRelativePingTimes();

            // Rise event: notify that pings are updated (in use by macOS implementation)
            OnPingsUpdated();
        }

        public async Task<ServerLocation> GetFastestServerAsync()
        {
            if (__LastPingTimeReceived == default(DateTime))
            {
                // waiting for pings update in background task
                GetPingParams(out var pingTimeoutMs, out var pingRetriesCount);
                await Task.Run(() => System.Threading.SpinWait.SpinUntil(() => __LastPingTimeReceived != default(DateTime),
                                                                         pingTimeoutMs + 100));
            }

            return DoFindFastestServer();
        }

        public ServerLocation GetFastestServer()
        {
            return DoFindFastestServer();
        }

        public bool StartPingUpdate()
        {
            if (!ServersList.Any())
                return false;

            if ((DateTime.Now - __LastPingTimeReceived).TotalMilliseconds < MinPingCheckIntervalMs)
                return false;

            RequestPings();
            return true;
        }

        public ServerLocation GetServerByIP(IPAddress addr, VpnType vpnType)
        {
            var allSvrs = __AllVpnServers;
            if (vpnType == VpnType.OpenVPN)
            {
                foreach (var s in allSvrs.OpenVPNServers)
                {
                    if (s.IsContainHostIpAddress(addr.ToString()))
                        return new ServerLocation(s);
                }
            }
            else if (vpnType == VpnType.WireGuard)
            {
                foreach (var s in allSvrs.WireGuardServers)
                {
                    if (s.IsContainHostIpAddress(addr.ToString()))
                        return new ServerLocation(s);
                }
            }

            return null;
        }

        public ServerLocation GetServerByOpenVpnMultihopID(string multihopId)
        {
            if (string.IsNullOrEmpty(multihopId))
                return null;

            var allSvrs = __AllVpnServers;
            foreach (var s in allSvrs.OpenVPNServers)
            {
                if (multihopId == s.GetMultihopId())
                    return new ServerLocation(s);
            }

            return null;
        }

        #region Dns servers info
        public IPAddress GetDnsIp(DnsTypeEnum dnsType, bool isMultihop)
        {
            if (__AllVpnServers?.Config?.AntiTracker == null)
                return null;

            string dns = "";

            switch (dnsType)
            {
                case DnsTypeEnum.AntiTracker:
                    if (__AllVpnServers.Config.AntiTracker.Default == null)
                        return null;

                    if (isMultihop && !string.IsNullOrEmpty(__AllVpnServers.Config.AntiTracker.Default.MultihopIp))
                        dns = __AllVpnServers.Config.AntiTracker.Default.MultihopIp;
                    else
                        dns = __AllVpnServers.Config.AntiTracker.Default.Ip;
                    break;

                case DnsTypeEnum.AntiTrackerHardcore:
                    if (__AllVpnServers.Config.AntiTracker.Hardcore == null)
                        return null;

                    if (isMultihop && !string.IsNullOrEmpty(__AllVpnServers.Config.AntiTracker.Hardcore.MultihopIp) )
                        dns = __AllVpnServers.Config.AntiTracker.Hardcore.MultihopIp;
                    else
                        dns = __AllVpnServers.Config.AntiTracker.Hardcore.Ip;
                    break;
            }

            if (IPAddress.TryParse(dns, out IPAddress dnsIp) == false)
                return null;

            return dnsIp;
        }

        public bool IsAntitracker(IPAddress dns, out bool isHardcore, out bool isMultihop)
        {
            isHardcore = false;
            isMultihop = false;

            if (dns == null)
                return false;

            string dnsIP = dns.ToString();

            if (string.Equals(__AllVpnServers?.Config?.AntiTracker?.Default?.Ip, dnsIP))
            {
                return true;
            }
            if (string.Equals(__AllVpnServers?.Config?.AntiTracker?.Hardcore?.Ip, dnsIP))
            {
                isHardcore = true;
                return true;
            }
            if (string.Equals(__AllVpnServers?.Config?.AntiTracker?.Default?.MultihopIp, dnsIP))
            {
                isMultihop = true;
                return true;
            }
            if (string.Equals(__AllVpnServers?.Config?.AntiTracker?.Hardcore?.MultihopIp, dnsIP))
            {
                isHardcore = true;
                isMultihop = true;
                return true;
            }

            return false;
        } 
        #endregion //Dns servers info

        #region Private implementation

        private ServerLocation DoFindFastestServer()
        {
            ServerLocation ret = null;
            ServerLocation retTmp = null;
            try
            {
                var servers = __ServersList;
                foreach (ServerLocation serverLocation in servers)
                {
                    try
                    {
                        if (!__AppSettings.ServersFilter.IsFastestServerInUse(serverLocation.VpnServer.GatewayId, __AppSettings.VpnProtocolType))
                            continue;
                    }
                    catch (Exception ex)
                    {
                        Logging.Info("EXCEPTION: DoFindFastestServer() - ServersFilter check error: " + ex);
                        // ignore all 
                    }

                    // save first possible location (will return it if all other locations have ret.PingTime == 0)
                    if (retTmp == null)
                        retTmp = serverLocation;

                    if (ret == null)
                        ret = serverLocation;
                    else if (ret.PingTime == 0 && serverLocation.PingTime > 0)
                        ret = serverLocation;
                    else if (serverLocation.PingTime > 0)
                    {
                        if (serverLocation.PingTime < ret.PingTime)
                            ret = serverLocation;
                    }
                }

                if (ret == null || ret.PingTime == 0)
                {
                    if (retTmp == null)
                        return null;

                    ret = retTmp;
                }

                // notify event: fastest server detected
                Task.Factory.StartNew(() => OnFasterServerDetected(ret));
            }
            catch
            {
                // ignored
            }
            
            return ret;
        }

        /// <summary>
        /// Recalculate relative-ping-time for all ping-results
        /// Relative ping time [0.0 - 1.0]: 
        ///     0 - small ping (green on GUI)
        ///     1 - big ping (red on GUI)
        /// Determine ping "quality" in comparison to ping-result from other servers. 
        /// </summary>
        private void RecalculateRelativePingTimes()
        {
            var servers = __ServersList;

            // if all servers have ping-time = 0  - return
            if (servers.All(server => server.PingTime == 0))
                return;

            int maxPing = servers.Select(s => s.PingTime).Max();
            int minPing = servers.Select(s => s.PingTime).Where(pingTime => pingTime != 0).Min();

            int distance = maxPing - minPing;

            foreach (var server in servers)
            {
                if (distance == 0)
                    server.PingTimeRelative = 0;
                else
                    server.PingTimeRelative = (server.PingTime - minPing) / (double)distance;
            }
        }
        #endregion //Private implementation
    }
}
