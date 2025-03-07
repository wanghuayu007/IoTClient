﻿using IoTClient.Common.Helpers;
using IoTClient.Enums;
using IoTClient.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace IoTClient.Clients.Modbus
{
    /// <summary>
    /// ModbusTcp协议客户端
    /// </summary>
    public class ModbusTcpClient : SocketBase, IModbusClient
    {
        private IPEndPoint ipAndPoint;
        private int timeout = -1;
        private EndianFormat format;

        /// <summary>
        /// 警告日志委托
        /// 为了可用性，会对异常网络已经进行重试。此类日志通过委托接口给出去。
        /// </summary>
        public LoggerDelegate WarningLog { get; set; }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="ipAndPoint"></param>
        /// <param name="timeout">超时时间（毫秒）</param>
        /// <param name="format">大小端设置</param>
        public ModbusTcpClient(IPEndPoint ipAndPoint, int timeout = 1500, EndianFormat format = EndianFormat.ABCD)
        {
            this.timeout = timeout;
            this.ipAndPoint = ipAndPoint;
            this.format = format;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="ip"></param>
        /// <param name="port"></param>
        /// <param name="timeout">超时时间（毫秒）</param>
        /// <param name="format">大小端设置</param>
        public ModbusTcpClient(string ip, int port, int timeout = 1500, EndianFormat format = EndianFormat.ABCD)
        {
            this.timeout = timeout;
            this.ipAndPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            this.format = format;
        }

        /// <summary>
        /// 连接
        /// </summary>
        /// <returns></returns>
        protected override Result Connect()
        {
            var result = new Result();
            socket?.Close();
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                //超时时间设置
                socket.ReceiveTimeout = timeout;
                socket.SendTimeout = timeout;
#if DEBUG               
                socket.ReceiveTimeout *= 10;
                socket.SendTimeout *= 10;
#endif                 

                //连接
                socket.Connect(ipAndPoint);
                return result;
            }
            catch (Exception ex)
            {
                socket?.SafeClose();
                result.IsSucceed = false;
                result.Err = ex.Message;
                return result;
            }
        }

        #region 发送报文，并获取响应报文
        /// <summary>
        /// 发送报文，并获取响应报文
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        public byte[] SendPackage(byte[] command)
        {
            //从发送命令到读取响应为最小单元，避免多线程执行串数据（可线程安全执行）
            lock (this)
            {
                //发送命令               
                try
                {
                    socket.Send(command);
                }
                catch (Exception ex)
                {
                    WarningLog?.Invoke(ex.Message, ex);
                    //如果出现异常，则进行一次重试         
                    Connect();
                    //TODO 
                    //1、判断result.IsSucceed
                    //2、Rtu、Ascii、TcpRtu 没进行重试
                    socket.Send(command);
                }

                //获取响应报文
                //如果出现异常，则进行一次重试  
                var headPackage = SocketTryRead(socket, 8, WarningLog);
                if (headPackage == null)
                {
                    Connect();
                    socket.Send(command);
                    headPackage = SocketRead(socket, 8);
                }
                int length = headPackage[4] * 256 + headPackage[5] - 2;
                var dataPackage = SocketRead(socket, length);
                return headPackage.Concat(dataPackage).ToArray();
            }
        }
        #endregion

        #region Read 读取
        /// <summary>
        /// 读取数据
        /// </summary>
        /// <param name="address">寄存器起始地址</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        /// <param name="readLength">读取长度</param>
        /// <param name="byteFormatting">大小端转换</param>
        /// <returns></returns>
        public Result<byte[]> Read(string address, byte stationNumber = 1, byte functionCode = 3, ushort readLength = 1, bool byteFormatting = true)
        {
            if (!socket?.Connected ?? true) Connect();
            var result = new Result<byte[]>();
            try
            {
                var chenkHead = GetCheckHead(functionCode);
                //1 获取命令（组装报文）
                byte[] command = GetReadCommand(address, stationNumber, functionCode, readLength, chenkHead);
                result.Requst = string.Join(" ", command.Select(t => t.ToString("X2")));
                //获取响应报文
                var dataPackage = SendPackage(command);
                byte[] resultBuffer = new byte[dataPackage.Length - 9];
                Array.Copy(dataPackage, 9, resultBuffer, 0, resultBuffer.Length);
                result.Response = string.Join(" ", dataPackage.Select(t => t.ToString("X2")));
                //4 获取响应报文数据（字节数组形式）             
                if (byteFormatting)
                    result.Value = resultBuffer.Reverse().ToArray().ByteFormatting(format);
                else
                    result.Value = resultBuffer.Reverse().ToArray();

                if (chenkHead[0] != dataPackage[0] || chenkHead[1] != dataPackage[1])
                {
                    result.IsSucceed = false;
                    result.Err = "响应结果校验失败";
                    result.ErrList.Add("响应结果校验失败");
                }
            }
            catch (SocketException ex)
            {
                result.IsSucceed = false;
                if (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    result.Err = "连接超时";
                    result.ErrList.Add("连接超时");
                    socket?.SafeClose();
                }
                else
                {
                    result.Err = ex.Message;
                    result.ErrList.Add(ex.Message);
                }
            }
            finally
            {
                if (isAutoOpen) Dispose();
            }
            return result;
        }

        /// <summary>
        /// 读取Int16
        /// </summary>
        /// <param name="address">寄存器起始地址</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        /// <returns></returns>
        public Result<short> ReadInt16(string address, byte stationNumber = 1, byte functionCode = 3)
        {
            var readResut = Read(address, stationNumber, functionCode);
            var result = new Result<short>()
            {
                IsSucceed = readResut.IsSucceed,
                Err = readResut.Err,
                ErrList = readResut.ErrList,
                Requst = readResut.Requst,
                Response = readResut.Response,
            };
            if (result.IsSucceed)
                result.Value = BitConverter.ToInt16(readResut.Value, 0);
            return result;
        }

        public Result<short> ReadInt16(int address, byte stationNumber = 1, byte functionCode = 3)
        {
            return ReadInt16(address.ToString(), stationNumber, functionCode);
        }

        /// <summary>
        /// 读取UInt16
        /// </summary>
        /// <param name="address">寄存器起始地址</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        /// <returns></returns>
        public Result<ushort> ReadUInt16(string address, byte stationNumber = 1, byte functionCode = 3)
        {
            var readResut = Read(address, stationNumber, functionCode);
            var result = new Result<ushort>()
            {
                IsSucceed = readResut.IsSucceed,
                Err = readResut.Err,
                ErrList = readResut.ErrList,
                Requst = readResut.Requst,
                Response = readResut.Response,
            };
            if (result.IsSucceed)
                result.Value = BitConverter.ToUInt16(readResut.Value, 0);
            return result;
        }

        public Result<ushort> ReadUInt16(int address, byte stationNumber = 1, byte functionCode = 3)
        {
            return ReadUInt16(address.ToString(), stationNumber, functionCode);
        }

        /// <summary>
        /// 读取Int32
        /// </summary>
        /// <param name="address">寄存器起始地址</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        /// <returns></returns>
        public Result<int> ReadInt32(string address, byte stationNumber = 1, byte functionCode = 3)
        {
            var readResut = Read(address, stationNumber, functionCode, readLength: 2);
            var result = new Result<int>()
            {
                IsSucceed = readResut.IsSucceed,
                Err = readResut.Err,
                ErrList = readResut.ErrList,
                Requst = readResut.Requst,
                Response = readResut.Response,
            };
            if (result.IsSucceed)
                result.Value = BitConverter.ToInt32(readResut.Value, 0);
            return result;
        }

        public Result<int> ReadInt32(int address, byte stationNumber = 1, byte functionCode = 3)
        {
            return ReadInt32(address.ToString(), stationNumber, functionCode);
        }


        /// <summary>
        /// 读取UInt32
        /// </summary>
        /// <param name="address">寄存器起始地址</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        /// <returns></returns>
        public Result<uint> ReadUInt32(string address, byte stationNumber = 1, byte functionCode = 3)
        {
            var readResut = Read(address, stationNumber, functionCode, readLength: 2);
            var result = new Result<uint>()
            {
                IsSucceed = readResut.IsSucceed,
                Err = readResut.Err,
                ErrList = readResut.ErrList,
                Requst = readResut.Requst,
                Response = readResut.Response,
            };
            if (result.IsSucceed)
                result.Value = BitConverter.ToUInt32(readResut.Value, 0);
            return result;
        }

        public Result<uint> ReadUInt32(int address, byte stationNumber = 1, byte functionCode = 3)
        {
            return ReadUInt32(address.ToString(), stationNumber, functionCode);
        }

        /// <summary>
        /// 读取Int64
        /// </summary>
        /// <param name="address">寄存器起始地址</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        /// <returns></returns>
        public Result<long> ReadInt64(string address, byte stationNumber = 1, byte functionCode = 3)
        {
            var readResut = Read(address, stationNumber, functionCode, readLength: 4);
            var result = new Result<long>()
            {
                IsSucceed = readResut.IsSucceed,
                Err = readResut.Err,
                ErrList = readResut.ErrList,
                Requst = readResut.Requst,
                Response = readResut.Response,
            };
            if (result.IsSucceed)
                result.Value = BitConverter.ToInt64(readResut.Value, 0);
            return result;
        }

        public Result<long> ReadInt64(int address, byte stationNumber = 1, byte functionCode = 3)
        {
            return ReadInt64(address.ToString(), stationNumber, functionCode);
        }

        /// <summary>
        /// 读取UInt64
        /// </summary>
        /// <param name="address">寄存器起始地址</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        /// <returns></returns>
        public Result<ulong> ReadUInt64(string address, byte stationNumber = 1, byte functionCode = 3)
        {
            var readResut = Read(address, stationNumber, functionCode, readLength: 4);
            var result = new Result<ulong>()
            {
                IsSucceed = readResut.IsSucceed,
                Err = readResut.Err,
                ErrList = readResut.ErrList,
                Requst = readResut.Requst,
                Response = readResut.Response,
            };
            if (result.IsSucceed)
                result.Value = BitConverter.ToUInt64(readResut.Value, 0);
            return result;
        }

        public Result<ulong> ReadUInt64(int address, byte stationNumber = 1, byte functionCode = 3)
        {
            return ReadUInt64(address.ToString(), stationNumber, functionCode);
        }

        /// <summary>
        /// 读取Float
        /// </summary>
        /// <param name="address">寄存器起始地址</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        /// <returns></returns>
        public Result<float> ReadFloat(string address, byte stationNumber = 1, byte functionCode = 3)
        {
            var readResut = Read(address, stationNumber, functionCode, readLength: 2);
            var result = new Result<float>()
            {
                IsSucceed = readResut.IsSucceed,
                Err = readResut.Err,
                ErrList = readResut.ErrList,
                Requst = readResut.Requst,
                Response = readResut.Response,
            };
            if (result.IsSucceed)
                result.Value = BitConverter.ToSingle(readResut.Value, 0);
            return result;
        }

        public Result<float> ReadFloat(int address, byte stationNumber = 1, byte functionCode = 3)
        {
            return ReadFloat(address.ToString(), stationNumber, functionCode);
        }

        /// <summary>
        /// 读取Double
        /// </summary>
        /// <param name="address">寄存器起始地址</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        /// <returns></returns>
        public Result<double> ReadDouble(string address, byte stationNumber = 1, byte functionCode = 3)
        {
            var readResut = Read(address, stationNumber, functionCode, readLength: 4);
            var result = new Result<double>()
            {
                IsSucceed = readResut.IsSucceed,
                Err = readResut.Err,
                ErrList = readResut.ErrList,
                Requst = readResut.Requst,
                Response = readResut.Response,
            };
            if (result.IsSucceed)
                result.Value = BitConverter.ToDouble(readResut.Value, 0);
            return result;
        }

        public Result<double> ReadDouble(int address, byte stationNumber = 1, byte functionCode = 3)
        {
            return ReadDouble(address.ToString(), stationNumber, functionCode);
        }

        /// <summary>
        /// 读取线圈
        /// </summary>
        /// <param name="address">寄存器起始地址</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        /// <returns></returns>
        public Result<bool> ReadCoil(string address, byte stationNumber = 1, byte functionCode = 1)
        {
            var readResut = Read(address, stationNumber, functionCode);
            var result = new Result<bool>()
            {
                IsSucceed = readResut.IsSucceed,
                Err = readResut.Err,
                ErrList = readResut.ErrList,
                Requst = readResut.Requst,
                Response = readResut.Response,
            };
            if (result.IsSucceed)
                result.Value = BitConverter.ToBoolean(readResut.Value, 0);
            return result;
        }

        public Result<bool> ReadCoil(int address, byte stationNumber = 1, byte functionCode = 3)
        {
            return ReadCoil(address.ToString(), stationNumber, functionCode);
        }

        /// <summary>
        /// 读取离散
        /// </summary>
        /// <param name="address">读取地址</param>
        /// <param name="stationNumber"></param>
        /// <param name="functionCode"></param>
        /// <returns></returns>
        public Result<bool> ReadDiscrete(string address, byte stationNumber = 1, byte functionCode = 2)
        {
            var readResut = Read(address, stationNumber, functionCode);
            var result = new Result<bool>()
            {
                IsSucceed = readResut.IsSucceed,
                Err = readResut.Err,
                ErrList = readResut.ErrList,
                Requst = readResut.Requst,
                Response = readResut.Response,
            };
            if (result.IsSucceed)
                result.Value = BitConverter.ToBoolean(readResut.Value, 0);
            return result;
        }

        public Result<bool> ReadDiscrete(int address, byte stationNumber = 1, byte functionCode = 3)
        {
            return ReadDiscrete(address.ToString(), stationNumber, functionCode);
        }

        /// <summary>
        /// 从批量读取的数据字节提取对应的地址数据
        /// </summary>
        /// <param name="beginAddress">批量读取的起始地址</param>
        /// <param name="address">读取地址</param>
        /// <param name="values">批量读取的值</param>
        /// <returns></returns>
        public Result<short> ReadInt16(string beginAddress, string address, byte[] values)
        {
            if (!int.TryParse(address?.Trim(), out int addressInt) || !int.TryParse(beginAddress?.Trim(), out int beginAddressInt))
                throw new Exception($"只能是数字，参数address：{address}  beginAddress：{beginAddress}");
            try
            {
                var i = addressInt - beginAddressInt;
                var byteArry = values.Skip(i * 2).Take(2).Reverse().ToArray();
                return new Result<short>
                {
                    Value = BitConverter.ToInt16(byteArry, 0)
                };
            }
            catch (Exception ex)
            {
                return new Result<short>
                {
                    IsSucceed = false,
                    Err = ex.Message
                };
            }
        }

        public Result<short> ReadInt16(int beginAddress, int address, byte[] values)
        {
            return ReadInt16(beginAddress.ToString(), address.ToString(), values);
        }

        /// <summary>
        /// 从批量读取的数据字节提取对应的地址数据
        /// </summary>
        /// <param name="beginAddress">批量读取的起始地址</param>
        /// <param name="address">读取地址</param>
        /// <param name="values">批量读取的值</param>
        /// <returns></returns>
        public Result<ushort> ReadUInt16(string beginAddress, string address, byte[] values)
        {
            if (!int.TryParse(address?.Trim(), out int addressInt) || !int.TryParse(beginAddress?.Trim(), out int beginAddressInt))
                throw new Exception($"只能是数字，参数address：{address}  beginAddress：{beginAddress}");
            try
            {
                var i = addressInt - beginAddressInt;
                var byteArry = values.Skip(i * 2).Take(2).Reverse().ToArray();
                return new Result<ushort>
                {
                    Value = BitConverter.ToUInt16(byteArry, 0)
                };
            }
            catch (Exception ex)
            {
                return new Result<ushort>
                {
                    IsSucceed = false,
                    Err = ex.Message
                };
            }
        }

        public Result<ushort> ReadUInt16(int beginAddress, int address, byte[] values)
        {
            return ReadUInt16(beginAddress.ToString(), address.ToString(), values);
        }

        /// <summary>
        /// 从批量读取的数据字节提取对应的地址数据
        /// </summary>
        /// <param name="beginAddress">批量读取的起始地址</param>
        /// <param name="address">读取地址</param>
        /// <param name="values">批量读取的值</param>
        /// <returns></returns>
        public Result<int> ReadInt32(string beginAddress, string address, byte[] values)
        {
            if (!int.TryParse(address?.Trim(), out int addressInt) || !int.TryParse(beginAddress?.Trim(), out int beginAddressInt))
                throw new Exception($"只能是数字，参数address：{address}  beginAddress：{beginAddress}");
            try
            {
                var i = (addressInt - beginAddressInt) / 2;
                var byteArry = values.Skip(i * 2 * 2).Take(2 * 2).Reverse().ToArray().ByteFormatting(format);
                return new Result<int>
                {
                    Value = BitConverter.ToInt32(byteArry, 0)
                };
            }
            catch (Exception ex)
            {
                return new Result<int>
                {
                    IsSucceed = false,
                    Err = ex.Message
                };
            }
        }

        public Result<int> ReadInt32(int beginAddress, int address, byte[] values)
        {
            return ReadInt32(beginAddress.ToString(), address.ToString(), values);
        }

        /// <summary>
        /// 从批量读取的数据字节提取对应的地址数据
        /// </summary>
        /// <param name="beginAddress">批量读取的起始地址</param>
        /// <param name="address">读取地址</param>
        /// <param name="values">批量读取的值</param>
        /// <returns></returns>
        public Result<uint> ReadUInt32(string beginAddress, string address, byte[] values)
        {
            if (!int.TryParse(address?.Trim(), out int addressInt) || !int.TryParse(beginAddress?.Trim(), out int beginAddressInt))
                throw new Exception($"只能是数字，参数address：{address}  beginAddress：{beginAddress}");
            try
            {
                var i = (addressInt - beginAddressInt) / 2;
                var byteArry = values.Skip(i * 2 * 2).Take(2 * 2).Reverse().ToArray().ByteFormatting(format);
                return new Result<uint>
                {
                    Value = BitConverter.ToUInt32(byteArry, 0)
                };
            }
            catch (Exception ex)
            {
                return new Result<uint>
                {
                    IsSucceed = false,
                    Err = ex.Message
                };
            }
        }

        public Result<uint> ReadUInt32(int beginAddress, int address, byte[] values)
        {
            return ReadUInt32(beginAddress.ToString(), address.ToString(), values);
        }

        /// <summary>
        /// 从批量读取的数据字节提取对应的地址数据
        /// </summary>
        /// <param name="beginAddress">批量读取的起始地址</param>
        /// <param name="address">读取地址</param>
        /// <param name="values">批量读取的值</param>
        /// <returns></returns>
        public Result<long> ReadInt64(string beginAddress, string address, byte[] values)
        {
            if (!int.TryParse(address?.Trim(), out int addressInt) || !int.TryParse(beginAddress?.Trim(), out int beginAddressInt))
                throw new Exception($"只能是数字，参数address：{address}  beginAddress：{beginAddress}");
            try
            {
                var i = (addressInt - beginAddressInt) / 4;
                var byteArry = values.Skip(i * 2 * 4).Take(2 * 4).Reverse().ToArray().ByteFormatting(format);
                return new Result<long>
                {
                    Value = BitConverter.ToInt64(byteArry, 0)
                };
            }
            catch (Exception ex)
            {
                return new Result<long>
                {
                    IsSucceed = false,
                    Err = ex.Message
                };
            }
        }

        public Result<long> ReadInt64(int beginAddress, int address, byte[] values)
        {
            return ReadInt64(beginAddress.ToString(), address.ToString(), values);
        }

        /// <summary>
        /// 从批量读取的数据字节提取对应的地址数据
        /// </summary>
        /// <param name="beginAddress">批量读取的起始地址</param>
        /// <param name="address">读取地址</param>
        /// <param name="values">批量读取的值</param>
        /// <returns></returns>
        public Result<ulong> ReadUInt64(string beginAddress, string address, byte[] values)
        {
            if (!int.TryParse(address?.Trim(), out int addressInt) || !int.TryParse(beginAddress?.Trim(), out int beginAddressInt))
                throw new Exception($"只能是数字，参数address：{address}  beginAddress：{beginAddress}");
            try
            {
                var i = (addressInt - beginAddressInt) / 4;
                var byteArry = values.Skip(i * 2 * 4).Take(2 * 4).Reverse().ToArray().ByteFormatting(format);
                return new Result<ulong>
                {
                    Value = BitConverter.ToUInt64(byteArry, 0)
                };
            }
            catch (Exception ex)
            {
                return new Result<ulong>
                {
                    IsSucceed = false,
                    Err = ex.Message
                };
            }
        }

        public Result<ulong> ReadUInt64(int beginAddress, int address, byte[] values)
        {
            return ReadUInt64(beginAddress.ToString(), address.ToString(), values);
        }

        /// <summary>
        /// 从批量读取的数据字节提取对应的地址数据
        /// </summary>
        /// <param name="beginAddress">批量读取的起始地址</param>
        /// <param name="address">读取地址</param>
        /// <param name="values">批量读取的值</param>
        /// <returns></returns>
        public Result<float> ReadFloat(string beginAddress, string address, byte[] values)
        {
            if (!int.TryParse(address?.Trim(), out int addressInt) || !int.TryParse(beginAddress?.Trim(), out int beginAddressInt))
                throw new Exception($"只能是数字，参数address：{address}  beginAddress：{beginAddress}");
            try
            {
                var i = (addressInt - beginAddressInt) / 2;
                var byteArry = values.Skip(i * 2 * 2).Take(2 * 2).Reverse().ToArray().ByteFormatting(format);
                return new Result<float>
                {
                    Value = BitConverter.ToSingle(byteArry, 0)
                };
            }
            catch (Exception ex)
            {
                return new Result<float>
                {
                    IsSucceed = false,
                    Err = ex.Message
                };
            }
        }

        public Result<float> ReadFloat(int beginAddress, int address, byte[] values)
        {
            return ReadFloat(beginAddress.ToString(), address.ToString(), values);
        }

        /// <summary>
        /// 从批量读取的数据字节提取对应的地址数据
        /// </summary>
        /// <param name="beginAddress">批量读取的起始地址</param>
        /// <param name="address">读取地址</param>
        /// <param name="values">批量读取的值</param>
        /// <returns></returns>
        public Result<double> ReadDouble(string beginAddress, string address, byte[] values)
        {
            if (!int.TryParse(address?.Trim(), out int addressInt) || !int.TryParse(beginAddress?.Trim(), out int beginAddressInt))
                throw new Exception($"只能是数字，参数address：{address}  beginAddress：{beginAddress}");
            try
            {
                var i = (addressInt - beginAddressInt) / 4;
                var byteArry = values.Skip(i * 2 * 4).Take(2 * 4).Reverse().ToArray().ByteFormatting(format);
                return new Result<double>
                {
                    Value = BitConverter.ToDouble(byteArry, 0)
                };
            }
            catch (Exception ex)
            {
                return new Result<double>
                {
                    IsSucceed = false,
                    Err = ex.Message
                };
            }
        }

        public Result<double> ReadDouble(int beginAddress, int address, byte[] values)
        {
            return ReadDouble(beginAddress.ToString(), address.ToString(), values);
        }

        /// <summary>
        /// 从批量读取的数据字节提取对应的地址数据
        /// </summary>
        /// <param name="beginAddress">批量读取的起始地址</param>
        /// <param name="address">读取地址</param>
        /// <param name="values">批量读取的值</param>
        /// <returns></returns>
        public Result<bool> ReadCoil(string beginAddress, string address, byte[] values)
        {
            if (!int.TryParse(address?.Trim(), out int addressInt) || !int.TryParse(beginAddress?.Trim(), out int beginAddressInt))
                throw new Exception($"只能是数字，参数address：{address}  beginAddress：{beginAddress}");
            try
            {
                var i = addressInt - beginAddressInt;
                var index = (i + 1) % 8 == 0 ? (i + 1) / 8 : (i + 1) / 8 + 1;
                var binaryArray = Convert.ToInt32(values[index - 1]).IntToBinaryArray().ToArray().Reverse().ToArray();
                var isBit = false;
                if ((index - 1) * 8 + binaryArray.Length > i)
                    isBit = binaryArray[i - (index - 1) * 8].ToString() == 1.ToString();
                return new Result<bool>()
                {
                    Value = isBit
                };
            }
            catch (Exception ex)
            {
                return new Result<bool>
                {
                    IsSucceed = false,
                    Err = ex.Message
                };
            }
        }

        public Result<bool> ReadCoil(int beginAddress, int address, byte[] values)
        {
            return ReadCoil(beginAddress.ToString(), address.ToString(), values);
        }

        /// <summary>
        /// 从批量读取的数据字节提取对应的地址数据
        /// </summary>
        /// <param name="beginAddress">批量读取的起始地址</param>
        /// <param name="address">读取地址</param>
        /// <param name="values">批量读取的值</param>
        /// <returns></returns>
        public Result<bool> ReadDiscrete(string beginAddress, string address, byte[] values)
        {
            if (!int.TryParse(address?.Trim(), out int addressInt) || !int.TryParse(beginAddress?.Trim(), out int beginAddressInt))
                throw new Exception($"只能是数字，参数address：{address}  beginAddress：{beginAddress}");
            try
            {
                var i = addressInt - beginAddressInt;
                var index = (i + 1) % 8 == 0 ? (i + 1) / 8 : (i + 1) / 8 + 1;
                var binaryArray = Convert.ToInt32(values[index - 1]).IntToBinaryArray().ToArray().Reverse().ToArray();
                var isBit = false;
                if ((index - 1) * 8 + binaryArray.Length > i)
                    isBit = binaryArray[i - (index - 1) * 8].ToString() == 1.ToString();
                return new Result<bool>()
                {
                    Value = isBit
                };
            }
            catch (Exception ex)
            {
                return new Result<bool>
                {
                    IsSucceed = false,
                    Err = ex.Message
                };
            }
        }

        public Result<bool> ReadDiscrete(int beginAddress, int address, byte[] values)
        {
            return ReadDiscrete(beginAddress.ToString(), address.ToString(), values);
        }

        #region 假的批量 注释
        ///// <summary>
        ///// 分批读取【假的批量，内部实际还是循环读取】
        ///// </summary>
        ///// <param name="addresses">地址集合</param>
        ///// <param name="batchNumber">批量读取数量</param>
        ///// <returns></returns>
        //public Result<Dictionary<string, object>> BatchRead(Dictionary<string, DataTypeEnum> addresses, int batchNumber = 19)
        //{
        //    var result = new Result<Dictionary<string, object>>();
        //    result.Value = new Dictionary<string, object>();

        //    var batchCount = Math.Ceiling((float)addresses.Count / batchNumber);
        //    for (int i = 0; i < batchCount; i++)
        //    {
        //        var tempAddresses = addresses.Skip(i * batchNumber).Take(batchNumber).ToDictionary(t => t.Key, t => t.Value);
        //        var tempResult = BatchRead(tempAddresses);
        //        if (tempResult.IsSucceed)
        //        {
        //            foreach (var item in tempResult.Value)
        //            {
        //                result.Value.Add(item.Key, item.Value);
        //            }
        //        }
        //        else
        //        {
        //            result.IsSucceed = false;
        //            result.Err = tempResult.Err;
        //        }
        //    }
        //    return result.EndTime();
        //}

        //private Result<Dictionary<string, object>> BatchRead(Dictionary<string, DataTypeEnum> addresses)
        //{
        //    var result = new Result<Dictionary<string, object>>();
        //    result.Value = new Dictionary<string, object>();
        //    try
        //    {
        //        foreach (var item in addresses)
        //        {
        //            var richText = item.Key.Split('_');
        //            if (richText.Length < 2)
        //                continue; //必须传入站号
        //            var stationNumber = byte.Parse(richText[0]);
        //            var addresse = richText[1];
        //            var functionCode = richText.Length >= 3 ? byte.Parse(richText[2]) : (byte)3;
        //            object value;
        //            switch (item.Value)
        //            {
        //                case DataTypeEnum.Bool:
        //                    value = ReadDiscrete(addresse, stationNumber, functionCode).Value;
        //                    break;
        //                //case DataTypeEnum.Byte:
        //                //    value = readResut[0];
        //                //    break;
        //                case DataTypeEnum.Int16:
        //                    value = ReadInt16(addresse, stationNumber, functionCode).Value;
        //                    break;
        //                case DataTypeEnum.UInt16:
        //                    value = ReadUInt16(addresse, stationNumber, functionCode).Value;
        //                    break;
        //                case DataTypeEnum.Int32:
        //                    value = ReadInt32(addresse, stationNumber, functionCode).Value;
        //                    break;
        //                case DataTypeEnum.UInt32:
        //                    value = ReadUInt32(addresse, stationNumber, functionCode).Value;
        //                    break;
        //                case DataTypeEnum.Int64:
        //                    value = ReadInt64(addresse, stationNumber, functionCode).Value;
        //                    break;
        //                case DataTypeEnum.UInt64:
        //                    value = ReadUInt64(addresse, stationNumber, functionCode).Value;
        //                    break;
        //                case DataTypeEnum.Float:
        //                    value = ReadFloat(addresse, stationNumber, functionCode).Value;
        //                    break;
        //                case DataTypeEnum.Double:
        //                    value = ReadDouble(addresse, stationNumber, functionCode).Value;
        //                    break;
        //                default:
        //                    throw new Exception($"未定义数据类型：{item.Value}");
        //            }
        //            result.Value.Add(item.Key, value);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        result.IsSucceed = false;
        //        result.Err = ex.Message;
        //        result.Exception = ex;
        //        result.ErrList.Add(ex.Message);
        //    }
        //    return result.EndTime();
        //} 
        #endregion

        /// <summary>
        /// 分批读取（批量读取，内部进行批量计算读取）
        /// </summary>
        /// <param name="addresses"></param>
        /// <returns></returns>
        public Result<List<ModbusOutput>> BatchRead(List<ModbusInput> addresses)
        {
            var result = new Result<List<ModbusOutput>>();
            result.Value = new List<ModbusOutput>();
            var functionCodes = addresses.Select(t => t.FunctionCode).Distinct();
            foreach (var functionCode in functionCodes)
            {
                var stationNumbers = addresses.Where(t => t.FunctionCode == functionCode).Select(t => t.StationNumber).Distinct();
                foreach (var stationNumber in stationNumbers)
                {
                    var addressList = addresses.Where(t => t.FunctionCode == functionCode && t.StationNumber == stationNumber)
                        .DistinctBy(t => t.Address)
                        .ToDictionary(t => t.Address, t => t.DataType);
                    var tempResult = BatchRead(addressList, stationNumber, functionCode);
                    if (tempResult.IsSucceed)
                    {
                        foreach (var item in tempResult.Value)
                        {
                            result.Value.Add(new ModbusOutput()
                            {
                                Address = item.Key,
                                FunctionCode = functionCode,
                                StationNumber = stationNumber,
                                Value = item.Value
                            });
                        }
                    }
                    else
                    {
                        result.IsSucceed = tempResult.IsSucceed;
                        result.Err = tempResult.Err;
                        result.ErrList.AddRange(tempResult.ErrList);
                    }
                }
            }
            return result.EndTime();
        }

        private Result<Dictionary<string, object>> BatchRead(Dictionary<string, DataTypeEnum> addressList, byte stationNumber, byte functionCode)
        {
            var result = new Result<Dictionary<string, object>>();
            result.Value = new Dictionary<string, object>();

            var addresses = addressList.Select(t => new KeyValuePair<int, DataTypeEnum>(int.Parse(t.Key), t.Value)).ToList();

            var minAddress = addresses.Select(t => t.Key).Min();
            var maxAddress = addresses.Select(t => t.Key).Max();
            while (maxAddress >= minAddress)
            {
                int readLength = 121;//125 - 4 = 121

                var tempAddress = addresses.Where(t => t.Key >= minAddress && t.Key <= minAddress + readLength).ToList();
                //如果范围内没有数据。按正确逻辑不存在这种情况。
                if (!tempAddress.Any())
                {
                    minAddress = minAddress + readLength;
                    continue;
                }

                var tempMax = tempAddress.OrderByDescending(t => t.Key).FirstOrDefault();
                switch (tempMax.Value)
                {
                    case DataTypeEnum.Bool:
                    case DataTypeEnum.Byte:
                    case DataTypeEnum.Int16:
                    case DataTypeEnum.UInt16:
                        readLength = tempMax.Key + 1 - minAddress;
                        break;
                    case DataTypeEnum.Int32:
                    case DataTypeEnum.UInt32:
                    case DataTypeEnum.Float:
                        readLength = tempMax.Key + 2 - minAddress;
                        break;
                    case DataTypeEnum.Int64:
                    case DataTypeEnum.UInt64:
                    case DataTypeEnum.Double:
                        readLength = tempMax.Key + 4 - minAddress;
                        break;
                    default:
                        throw new Exception("Err BatchRead 未定义类型 -1");
                }

                var tempResult = Read(minAddress.ToString(), stationNumber, functionCode, Convert.ToUInt16(readLength), false);

                if (!tempResult.IsSucceed)
                {
                    result.IsSucceed = tempResult.IsSucceed;
                    result.Exception = tempResult.Exception;
                    result.Err = tempResult.Err;
                    result.ErrList.AddRange(tempResult.ErrList);
                    return result;
                }

                var rValue = tempResult.Value.Reverse().ToArray();
                foreach (var item in tempAddress)
                {
                    object tempVaue = null;

                    switch (item.Value)
                    {
                        case DataTypeEnum.Bool:
                            tempVaue = ReadCoil(minAddress, item.Key, rValue).Value;
                            break;
                        case DataTypeEnum.Byte:
                            throw new Exception("Err BatchRead 未定义类型 -2");
                        case DataTypeEnum.Int16:
                            tempVaue = ReadInt16(minAddress, item.Key, rValue).Value;
                            break;
                        case DataTypeEnum.UInt16:
                            tempVaue = ReadUInt16(minAddress, item.Key, rValue).Value;
                            break;
                        case DataTypeEnum.Int32:
                            tempVaue = ReadInt32(minAddress, item.Key, rValue).Value;
                            break;
                        case DataTypeEnum.UInt32:
                            tempVaue = ReadUInt32(minAddress, item.Key, rValue).Value;
                            break;
                        case DataTypeEnum.Int64:
                            tempVaue = ReadInt64(minAddress, item.Key, rValue).Value;
                            break;
                        case DataTypeEnum.UInt64:
                            tempVaue = ReadUInt64(minAddress, item.Key, rValue).Value;
                            break;
                        case DataTypeEnum.Float:
                            tempVaue = ReadFloat(minAddress, item.Key, rValue).Value;
                            break;
                        case DataTypeEnum.Double:
                            tempVaue = ReadDouble(minAddress, item.Key, rValue).Value;
                            break;
                        default:
                            throw new Exception("Err BatchRead 未定义类型 -3");
                    }

                    result.Value.Add(item.Key.ToString(), tempVaue);
                }
                minAddress = minAddress + readLength;

                if (addresses.Any(t => t.Key >= minAddress))
                    minAddress = addresses.Where(t => t.Key >= minAddress).OrderBy(t => t.Key).FirstOrDefault().Key;
                else
                    return result;
            }
            return result;
        }

        #endregion

        #region Write 写入

        /// <summary>
        /// 线圈写入
        /// </summary>
        /// <param name="address">读取地址</param>
        /// <param name="value"></param>
        /// <param name="stationNumber"></param>
        /// <param name="functionCode"></param>
        public Result Write(string address, bool value, byte stationNumber = 1, byte functionCode = 5)
        {
            if (!socket?.Connected ?? true) Connect();
            var result = new Result();
            try
            {
                var chenkHead = GetCheckHead(functionCode);
                var command = GetWriteCoilCommand(address, value, stationNumber, functionCode, chenkHead);
                result.Requst = string.Join(" ", command.Select(t => t.ToString("X2")));
                var dataPackage = SendPackage(command);
                result.Response = string.Join(" ", dataPackage.Select(t => t.ToString("X2")));
                if (chenkHead[0] != dataPackage[0] || chenkHead[1] != dataPackage[1])
                {
                    result.IsSucceed = false;
                    result.Err = "响应结果校验失败";
                    result.ErrList.Add("响应结果校验失败");
                }
            }
            catch (SocketException ex)
            {
                result.IsSucceed = false;
                if (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    result.Err = "连接超时";
                    result.ErrList.Add("连接超时");
                    socket?.SafeClose();
                }
                else
                {
                    result.Err = ex.Message;
                    result.ErrList.Add(ex.Message);
                }
            }
            finally
            {
                if (isAutoOpen) Dispose();
            }
            return result;
        }

        /// <summary>
        /// 写入
        /// </summary>
        /// <param name="address">读取地址</param>
        /// <param name="values">批量读取的值</param>
        /// <param name="stationNumber"></param>
        /// <param name="functionCode"></param>
        /// <returns></returns>
        public Result Write(string address, byte[] values, byte stationNumber = 1, byte functionCode = 16)
        {
            if (!socket?.Connected ?? true) Connect();

            var result = new Result();
            try
            {
                values = values.ByteFormatting(format);
                var chenkHead = GetCheckHead(functionCode);
                var command = GetWriteCommand(address, values, stationNumber, functionCode, chenkHead);
                result.Requst = string.Join(" ", command.Select(t => t.ToString("X2")));
                var dataPackage = SendPackage(command);
                result.Response = string.Join(" ", dataPackage.Select(t => t.ToString("X2")));
                if (chenkHead[0] != dataPackage[0] || chenkHead[1] != dataPackage[1])
                {
                    result.IsSucceed = false;
                    result.Err = "响应结果校验失败";
                    result.ErrList.Add("响应结果校验失败");
                }
            }
            catch (SocketException ex)
            {
                result.IsSucceed = false;
                if (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    result.Err = "连接超时";
                    result.ErrList.Add("连接超时");
                    socket?.SafeClose();
                }
                else
                {
                    result.Err = ex.Message;
                    result.ErrList.Add(ex.Message);
                }
            }
            finally
            {
                if (isAutoOpen) Dispose();
            }
            return result;
        }

        /// <summary>
        /// 写入
        /// </summary>
        /// <param name="address">寄存器地址</param>
        /// <param name="value">写入的值</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        public Result Write(string address, short value, byte stationNumber = 1, byte functionCode = 16)
        {
            var values = BitConverter.GetBytes(value).Reverse().ToArray();
            return Write(address, values, stationNumber, functionCode);
        }

        /// <summary>
        /// 写入
        /// </summary>
        /// <param name="address">寄存器地址</param>
        /// <param name="value">写入的值</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        public Result Write(string address, ushort value, byte stationNumber = 1, byte functionCode = 16)
        {
            var values = BitConverter.GetBytes(value).Reverse().ToArray();
            return Write(address, values, stationNumber, functionCode);
        }

        /// <summary>
        /// 写入
        /// </summary>
        /// <param name="address">寄存器地址</param>
        /// <param name="value">写入的值</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        public Result Write(string address, int value, byte stationNumber = 1, byte functionCode = 16)
        {
            var values = BitConverter.GetBytes(value).Reverse().ToArray();
            return Write(address, values, stationNumber, functionCode);
        }

        /// <summary>
        /// 写入
        /// </summary>
        /// <param name="address">寄存器地址</param>
        /// <param name="value">写入的值</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        public Result Write(string address, uint value, byte stationNumber = 1, byte functionCode = 16)
        {
            var values = BitConverter.GetBytes(value).Reverse().ToArray();
            return Write(address, values, stationNumber, functionCode);
        }

        /// <summary>
        /// 写入
        /// </summary>
        /// <param name="address">寄存器地址</param>
        /// <param name="value">写入的值</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        public Result Write(string address, long value, byte stationNumber = 1, byte functionCode = 16)
        {
            var values = BitConverter.GetBytes(value).Reverse().ToArray();
            return Write(address, values, stationNumber, functionCode);
        }

        /// <summary>
        /// 写入
        /// </summary>
        /// <param name="address">寄存器地址</param>
        /// <param name="value">写入的值</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        public Result Write(string address, ulong value, byte stationNumber = 1, byte functionCode = 16)
        {
            var values = BitConverter.GetBytes(value).Reverse().ToArray();
            return Write(address, values, stationNumber, functionCode);
        }

        /// <summary>
        /// 写入
        /// </summary>
        /// <param name="address">寄存器地址</param>
        /// <param name="value">写入的值</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        public Result Write(string address, float value, byte stationNumber = 1, byte functionCode = 16)
        {
            var values = BitConverter.GetBytes(value).Reverse().ToArray();
            return Write(address, values, stationNumber, functionCode);
        }

        /// <summary>
        /// 写入
        /// </summary>
        /// <param name="address">寄存器地址</param>
        /// <param name="value">写入的值</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        public Result Write(string address, double value, byte stationNumber = 1, byte functionCode = 16)
        {
            var values = BitConverter.GetBytes(value).Reverse().ToArray();
            return Write(address, values, stationNumber, functionCode);
        }
        #endregion

        #region 获取命令

        /// <summary>
        /// 获取随机校验头
        /// </summary>
        /// <returns></returns>
        private byte[] GetCheckHead(int seed)
        {
            var random = new Random(DateTime.Now.Millisecond + seed);
            return new byte[] { (byte)random.Next(255), (byte)random.Next(255) };
        }

        /// <summary>
        /// 获取读取命令
        /// </summary>
        /// <param name="address">寄存器起始地址</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        /// <param name="length">读取长度</param>
        /// <returns></returns>
        public byte[] GetReadCommand(string address, byte stationNumber, byte functionCode, ushort length, byte[] check = null)
        {
            var readAddress = ushort.Parse(address?.Trim());
            byte[] buffer = new byte[12];
            buffer[0] = check?[0] ?? 0x19;
            buffer[1] = check?[1] ?? 0xB2;//Client发出的检验信息
            buffer[2] = 0x00;
            buffer[3] = 0x00;//表示tcp/ip 的协议的Modbus的协议
            buffer[4] = 0x00;
            buffer[5] = 0x06;//表示的是该字节以后的字节长度

            buffer[6] = stationNumber;  //站号
            buffer[7] = functionCode;   //功能码
            buffer[8] = BitConverter.GetBytes(readAddress)[1];
            buffer[9] = BitConverter.GetBytes(readAddress)[0];//寄存器地址
            buffer[10] = BitConverter.GetBytes(length)[1];
            buffer[11] = BitConverter.GetBytes(length)[0];//表示request 寄存器的长度(寄存器个数)
            return buffer;
        }

        /// <summary>
        /// 获取写入命令
        /// </summary>
        /// <param name="address">寄存器地址</param>
        /// <param name="values">批量读取的值</param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        /// <returns></returns>
        public byte[] GetWriteCommand(string address, byte[] values, byte stationNumber, byte functionCode, byte[] check = null)
        {
            var writeAddress = ushort.Parse(address?.Trim());
            byte[] buffer = new byte[13 + values.Length];
            buffer[0] = check?[0] ?? 0x19;
            buffer[1] = check?[1] ?? 0xB2;//检验信息，用来验证response是否串数据了           
            buffer[4] = BitConverter.GetBytes(7 + values.Length)[1];
            buffer[5] = BitConverter.GetBytes(7 + values.Length)[0];//表示的是header handle后面还有多长的字节

            buffer[6] = stationNumber; //站号
            buffer[7] = functionCode;  //功能码
            buffer[8] = BitConverter.GetBytes(writeAddress)[1];
            buffer[9] = BitConverter.GetBytes(writeAddress)[0];//寄存器地址
            buffer[10] = (byte)(values.Length / 2 / 256);
            buffer[11] = (byte)(values.Length / 2 % 256);//写寄存器数量(除2是两个字节一个寄存器，寄存器16位。除以256是byte最大存储255。)              
            buffer[12] = (byte)(values.Length);          //写字节的个数
            values.CopyTo(buffer, 13);                   //把目标值附加到数组后面
            return buffer;
        }

        /// <summary>
        /// 获取线圈写入命令
        /// </summary>
        /// <param name="address">寄存器地址</param>
        /// <param name="value"></param>
        /// <param name="stationNumber">站号</param>
        /// <param name="functionCode">功能码</param>
        /// <returns></returns>
        public byte[] GetWriteCoilCommand(string address, bool value, byte stationNumber, byte functionCode, byte[] check = null)
        {
            var writeAddress = ushort.Parse(address?.Trim());
            byte[] buffer = new byte[12];
            buffer[0] = check?[0] ?? 0x19;
            buffer[1] = check?[1] ?? 0xB2;//Client发出的检验信息     
            buffer[4] = 0x00;
            buffer[5] = 0x06;//表示的是该字节以后的字节长度

            buffer[6] = stationNumber;//站号
            buffer[7] = functionCode; //功能码
            buffer[8] = BitConverter.GetBytes(writeAddress)[1];
            buffer[9] = BitConverter.GetBytes(writeAddress)[0];//寄存器地址
            buffer[10] = (byte)(value ? 0xFF : 0x00);     //此处只可以是FF表示闭合00表示断开，其他数值非法
            buffer[11] = 0x00;
            return buffer;
        }

        #endregion      
    }
}
