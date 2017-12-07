﻿using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using GlobalPayments.Api.Entities;
using GlobalPayments.Api.Terminals.Abstractions;
using GlobalPayments.Api.Terminals.Messaging;
using GlobalPayments.Api.Terminals.Extensions;
using GlobalPayments.Api.Utils;

namespace GlobalPayments.Api.Terminals.HeartSIP.Interfaces {
    internal class HeartSipTcpInterface : IDeviceCommInterface {
        TcpClient _client;
        NetworkStream _stream;
        ITerminalConfiguration _settings;
        List<byte> message_queue;

        public event MessageSentEventHandler OnMessageSent;
        public event MessageReceivedEventHandler OnMessageReceived;

        public HeartSipTcpInterface(ITerminalConfiguration settings) {
            this._settings = settings;
        }

        private async Task<bool> BeginReceiveTask() {
            while (true) {
                try {
                    if (_stream == null)
                        return false;

                    if (_stream.DataAvailable) {
                        do {
                            var length = await _stream.GetLengthAsync();
                            if (length > 0) {
                                byte[] buffer = new byte[8192];

                                var incomplete = true;
                                int offset = 0;
                                int temp_length = length;
                                do {
                                    int bytesReceived = await _stream.ReadAsync(buffer, offset, temp_length);
                                    if (bytesReceived != temp_length) {
                                        offset += bytesReceived;
                                        temp_length -= bytesReceived;
                                    }
                                    else incomplete = false;
                                } while (incomplete);

                                var readBuffer = new byte[length];
                                Array.Copy(buffer, readBuffer, length);

                                if (MessageReceived(readBuffer))
                                    return true;
                            }
                            else break;
                        } while (true);
                    }
                }
                catch (Exception) {
                    return false;
                }
            }
        }

        public void Connect() {
            if (_client == null) {
                _client = new TcpClient();
                _client.ConnectAsync(_settings.IpAddress, int.Parse(_settings.Port)).Wait(_settings.TimeOut);
                _stream = _client.GetStream();
                _stream.ReadTimeout = 60000;
            }
        }

        public void Disconnect() {
            if (_stream != null) {
                _stream.Dispose();
                _stream = null;
            }

            if (_client != null) {
                _client.Dispose();
                _client = null;
            }
            message_queue = null;
        }

        public byte[] Send(IDeviceMessage message) {
            Connect();

            var str_message = message.ToString();
            message_queue = new List<byte>();
            try {
                byte[] buffer = message.GetSendBuffer();
                OnMessageSent?.Invoke(message.ToString().Substring(2));

                if (_stream != null) {
                    _stream.Write(buffer, 0, buffer.Length);
                    _stream.Flush();

                    var task = BeginReceiveTask();
                    if(!task.Wait(_settings.TimeOut))
                        throw new MessageException("Device did not respond within the timeout.");

                    return message_queue.ToArray();
                }
                else throw new MessageException("Device not connected.");
            }
            finally {
                Disconnect();
            }
        }

        private bool MessageReceived(byte[] buffer) {
            if (message_queue == null)
                return false;

            message_queue.AddRange(buffer);

            var msg = ElementTree.Parse(buffer).Get("SIP");
            var multiMessage = msg.GetValue<int>("MultipleMessage");
            var response = msg.GetValue<string>("Response");
            var text = msg.GetValue<string>("ResponseText");
            if (multiMessage != 0) {
                message_queue.Add((byte)'\r'); // Delimiting character
                return false;
            }
            else return true;
        }
    }
}
