﻿//Copyright(c) 2021 MultiFactor
//Please see licence at 
//https://github.com/MultifactorLab/MultiFactor.Ldap.Adapter/blob/main/LICENSE.md

using MultiFactor.Ldap.Adapter.Core;
using MultiFactor.Ldap.Adapter.Services;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MultiFactor.Ldap.Adapter.Server
{
    public class LdapProxy
    {
        private TcpClient _clientConnection;
        private TcpClient _serverConnection;
        private Stream _clientStream;
        private Stream _serverStream;
        private Configuration _configuration;
        private ILogger _logger;
        private string _userName;
        private string _lookupUserName;

        private LdapProxyAuthenticationStatus _status;

        private static readonly ConcurrentDictionary<string, string> _usersDn = new ConcurrentDictionary<string, string>();

        public LdapProxy(TcpClient clientConnection, Stream clientStream, TcpClient serverConnection, Stream serverStream, Configuration configuration, ILogger logger)
        {
            _clientConnection = clientConnection ?? throw new ArgumentNullException(nameof(clientConnection));
            _clientStream = clientStream ?? throw new ArgumentNullException(nameof(clientStream));
            _serverConnection = serverConnection ?? throw new ArgumentNullException(nameof(serverConnection));
            _serverStream = serverStream ?? throw new ArgumentNullException(nameof(serverStream));

            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task Start()
        {
            var from = _clientConnection.Client.RemoteEndPoint.ToString();
            var to = _serverConnection.Client.RemoteEndPoint.ToString();

            _logger.Debug($"Opened {from} => {to}");

            await Task.WhenAny(
                DataExchange(_clientConnection, _clientStream, _serverConnection, _serverStream, ParseAndProcessRequest),
                DataExchange(_serverConnection, _serverStream, _clientConnection, _clientStream, ParseAndProcessResponse));

            _logger.Debug($"Closed {from} => {to}");
        }

        private async Task DataExchange(TcpClient source, Stream sourceStream, TcpClient target, Stream targetStream, Func<byte[], int, (byte[], int)> process)
        {
            try
            {
                var bytesRead = 0;
                var requestData = new byte[8192];

                do
                {
                    //read
                    bytesRead = await sourceStream.ReadAsync(requestData, 0, requestData.Length);

                    //process
                    var response = process(requestData, bytesRead);

                    //write
                    await targetStream.WriteAsync(response.Item1, 0, response.Item2);

                    if (_status == LdapProxyAuthenticationStatus.AuthenticationFailed)
                    {
                        source.Close();
                    }

                } while (bytesRead != 0);
            }
            catch(IOException)
            {
                //connection closed unexpectly
                //_logger.Debug(ioex, "proxy");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Data exchange error from {source.Client.RemoteEndPoint} to {target.Client.RemoteEndPoint}");
            }
        }

        private (byte[], int) ParseAndProcessRequest(byte[] data, int length)
        {
            var packet = LdapPacket.ParsePacket(data);

            var searchRequest = packet.ChildAttributes.SingleOrDefault(c => c.LdapOperation == LdapOperation.SearchRequest);
            if (searchRequest != null)
            {
                var filter = searchRequest.ChildAttributes[6];
                if ((LdapFilterChoice)filter.ContextType == LdapFilterChoice.equalityMatch) // uid eq login
                {
                    var left = filter.ChildAttributes[0].GetValue<string>()?.ToLower();
                    var right = filter.ChildAttributes[1].GetValue<string>();

                    var userNameAttrs = new[] { "cn", "uid", "samaccountname" };

                    if (userNameAttrs.Any(attr => attr == left))
                    {
                        //user name lookup, from login to DN
                        //lets remember
                        _status = LdapProxyAuthenticationStatus.UserDnSearch;
                        _lookupUserName = right;
                    }
                }
            }

            var bindRequest = packet.ChildAttributes.SingleOrDefault(c => c.LdapOperation == LdapOperation.BindRequest);
            if (bindRequest != null)
            {
                var isSasl = bindRequest.ChildAttributes[2].IsConstructed;
                if (isSasl)
                {
                    ProcessSaslBind(bindRequest);
                }
                else
                {
                    ProcessSimpleBind(bindRequest);
                }
            }

            return (data, length);
        }

        private (byte[], int) ParseAndProcessResponse(byte[] data, int length)
        {
            if (_status == LdapProxyAuthenticationStatus.BindRequested)
            {
                var packet = LdapPacket.ParsePacket(data);
                var bindResponse = packet.ChildAttributes.SingleOrDefault(c => c.LdapOperation == LdapOperation.BindResponse);
            
                if (bindResponse != null)
                {
                    var bound = bindResponse.ChildAttributes[0].GetValue<LdapResult>() == LdapResult.success;

                    if (bound)  //first factor authenticated
                    {
                        _logger.Information($"User '{_userName}' credential verified successfully at {_serverConnection.Client.RemoteEndPoint}");

                        var apiClient = new MultiFactorApiClient(_configuration, _logger);
                        var result = apiClient.Authenticate(_userName); //second factor

                        if (!result) // second factor failed
                        {
                            //return invalid creds response
                            var responsePacket = InvalidCredentials(packet);
                            var response = responsePacket.GetBytes();

                            _logger.Warning($"Second factor authentication for user '{_userName}' failed");
                            _logger.Debug($"Sent invalid credential response for user '{_userName}' to {_clientConnection.Client.RemoteEndPoint}");

                            _status = LdapProxyAuthenticationStatus.AuthenticationFailed;

                            return (response, response.Length);
                        }

                        _status = LdapProxyAuthenticationStatus.None;
                    }
                    else //first factor authentication failed
                    {
                        //just log
                        var reason = bindResponse.ChildAttributes[2].GetValue<string>();
                        _logger.Warning($"Verification user '{_userName}' at {_serverConnection.Client.RemoteEndPoint} failed: {reason}");
                    }
                }
            }

            if (_status == LdapProxyAuthenticationStatus.UserDnSearch)
            {
                var packet = LdapPacket.ParsePacket(data);
                var searchResultEntry = packet.ChildAttributes.SingleOrDefault(c => c.LdapOperation == LdapOperation.SearchResultEntry);
                
                if (searchResultEntry != null)
                {
                    var userDn = searchResultEntry.ChildAttributes[0].GetValue<string>();

                    if (_lookupUserName != null && userDn != null)
                    {
                        _usersDn.TryRemove(userDn, out _);
                        _usersDn.TryAdd(userDn, _lookupUserName);
                    }
                }

                _status = LdapProxyAuthenticationStatus.None;
            }

            return (data, length);
        }

        private void ProcessSimpleBind(LdapAttribute bindRequest)
        {
            var bindDn = bindRequest.ChildAttributes[1].GetValue<string>();

            //empty userName means anonymous bind
            if (!string.IsNullOrEmpty(bindDn))
            {
                var uid = ConvertDistinguishedNameToUserName(bindDn);

                if (_configuration.ServiceAccounts.Any(acc => acc == uid.ToLower()))
                {
                    //service acc
                    _logger.Debug($"Received simple bind request for service account '{bindDn}' from {_clientConnection.Client.RemoteEndPoint}");
                }
                else
                {
                    //user acc
                    _userName = uid;
                    _status = LdapProxyAuthenticationStatus.BindRequested;
                    _logger.Debug($"Received simple bind request for user '{bindDn}' from {_clientConnection.Client.RemoteEndPoint}");
                }
            }
        }

        private void ProcessSaslBind(LdapAttribute bindRequest)
        {
            //todo
            //var mechanism = bindRequest.ChildAttributes[2].ChildAttributes[0].GetValue<string>();
            //var ntlmPacket = bindRequest.ChildAttributes[2].ChildAttributes[1].GetValue<byte[]>();
        }

        private string ConvertDistinguishedNameToUserName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            if (_usersDn.TryGetValue(name, out var userName))
            {
                return userName;
            }

            return name;
        }

        private LdapPacket InvalidCredentials(LdapPacket requestPacket)
        {
            var responsePacket = new LdapPacket(requestPacket.MessageId);
            responsePacket.ChildAttributes.Add(new LdapResultAttribute(LdapOperation.BindResponse, LdapResult.invalidCredentials));
            return responsePacket;
        }
    }

    public enum LdapProxyAuthenticationStatus
    {
        None,
        UserDnSearch,
        BindRequested,
        AuthenticationFailed
    }
}