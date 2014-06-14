#region Licence

// Copyright (C) 2012 by Jakub Bartkowiak (Gralin)
//
// MIT Licence
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

#endregion Licence

/* This is a modified version of Jakub Bartkowiak's nRF24L01p driver 
 * for the .NET Micro Framework. The primary change is the reversing
 * of address and message data to least signifigant byte first to 
 * comply with the nRF24L01+ datasheet spessifications. While the previous
 * versions worked fine, it was not interoperable with other drivers. This
 * presents a problem when talking to Arduino and Raspi implimentations
 * that followed the Nordic spec. 
 * 
 * I have also collapsed the previous project in to a single source file. 
 * While this has no effect on the driver, it makes it a little easier to
 * get up and running when you have a mix of boards running NETMF 4.1, 4.2 and 4.3
 * 
 * Marc LaFleur: 2014  
 */
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using System;
using System.Threading;

namespace NETMF.Nordic
{
    /// <summary>
    ///   Driver class for Nordic nRF24L01+ tranceiver
    /// </summary>
    public class NRF24L01Plus
    {
        private readonly ManualResetEvent _transmitFailedFlag;
        private readonly ManualResetEvent _transmitSuccessFlag;
        private OutputPort _cePin;
        private bool _enabled;
        private bool _initialized;
        private InterruptPort _irqPin;
        private byte[] _slot0Address;
        private SPI _spiPort;

        public NRF24L01Plus()
        {
            _transmitSuccessFlag = new ManualResetEvent(false);
            _transmitFailedFlag = new ManualResetEvent(false);
        }

        public delegate void EventHandler();

        public delegate void OnDataRecievedHandler(byte[] data);

        public delegate void OnInterruptHandler(Status status);

        /// <summary>
        ///   Occurs when data packet has been received
        /// </summary>
        public event OnDataRecievedHandler OnDataReceived = delegate { };

        /// <summary>
        ///   Called on every IRQ interrupt
        /// </summary>
        public event OnInterruptHandler OnInterrupt = delegate { };

        /// <summary>
        ///   Occurs when no ack has been received for send packet
        /// </summary>
        public event EventHandler OnTransmitFailed = delegate { };

        /// <summary>
        ///   Occurs when ack has been received for send packet
        /// </summary>
        public event EventHandler OnTransmitSuccess = delegate { };

        /// <summary>
        ///   Gets a value indicating whether module is enabled (RX or TX mode).
        /// </summary>
        public bool IsEnabled
        {
            get { return _cePin.Read(); }
        }

        /// <summary>
        /// Configure the module basic settings. Module needs to be initiaized.
        /// </summary>
        /// <param name="address">RF address (3-5 bytes). The width of this address determins the width of all addresses used for sending/receiving.</param>
        /// <param name="channel">RF channel (0-127)</param>
        public void Configure(byte[] address, byte channel)
        {
            Configure(address, channel, NRFDataRate.DR2Mbps);
        }

        /// <summary>
        /// Configure the module basic settings. Module needs to be initiaized.
        /// </summary>
        /// <param name="address">RF address (3-5 bytes). The width of this address determins the width of all addresses used for sending/receiving.</param>
        /// <param name="channel">RF channel (0-127)</param>
        /// <param name="dataRate">Data Rate to use</param>
        public void Configure(byte[] address, byte channel, NRFDataRate dataRate)
        {
            CheckIsInitialized();
            AddressWidth.Check(address);

            // Address must be LSByte to MSByte per nRF24L01+ Datasheet
            address = ByteReverse(address);

            // Set radio channel
            Execute(Commands.W_REGISTER, Registers.RF_CH,
                    new[]
                        {
                            (byte) (channel & 0x7F) // channel is 7 bits
                        });

            // Set Data rate
            var regValue = Execute(Commands.R_REGISTER, Registers.RF_SETUP, new byte[1])[1];

            switch (dataRate)
            {
                case NRFDataRate.DR1Mbps:
                    regValue &= (byte)~(1 << Bits.RF_DR_LOW);  // 0
                    regValue &= (byte)~(1 << Bits.RF_DR_HIGH); // 0
                    break;

                case NRFDataRate.DR2Mbps:
                    regValue &= (byte)~(1 << Bits.RF_DR_LOW);  // 0
                    regValue |= (byte)(1 << Bits.RF_DR_HIGH);  // 1
                    break;

                case NRFDataRate.DR250kbps:
                    regValue |= (byte)(1 << Bits.RF_DR_LOW);   // 1
                    regValue &= (byte)~(1 << Bits.RF_DR_HIGH); // 0
                    break;

                default:
                    throw new ArgumentOutOfRangeException("dataRate");
            }

            Execute(Commands.W_REGISTER, Registers.RF_SETUP, new[] { regValue });

            // Enable dynamic payload length
            Execute(Commands.W_REGISTER, Registers.FEATURE,
                    new[]
                        {
                            (byte) (1 << Bits.EN_DPL)
                        });

            // Set auto-ack
            Execute(Commands.W_REGISTER, Registers.EN_AA,
                    new[]
                        {
                            (byte) (1 << Bits.ENAA_P0 |
                                    1 << Bits.ENAA_P1)
                        });

            // Set dynamic payload length for pipes
            Execute(Commands.W_REGISTER, Registers.DYNPD,
                    new[]
                        {
                            (byte) (1 << Bits.DPL_P0 |
                                    1 << Bits.DPL_P1)
                        });

            // Flush RX FIFO
            Execute(Commands.FLUSH_RX, 0x00, new byte[0]);

            // Flush TX FIFO
            Execute(Commands.FLUSH_TX, 0x00, new byte[0]);

            // Clear IRQ Masks
            Execute(Commands.W_REGISTER, Registers.STATUS,
                    new[]
                        {
                            (byte) (1 << Bits.MASK_RX_DR |
                                    1 << Bits.MASK_TX_DS |
                                    1 << Bits.MAX_RT)
                        });

            // Set default address
            Execute(Commands.W_REGISTER, Registers.SETUP_AW,
                    new[]
                        {
                            AddressWidth.Get(address)
                        });

            // Set module address
            _slot0Address = address;
            Execute(Commands.W_REGISTER, (byte)AddressSlot.Zero, address);

            // Set retransmission values
            Execute(Commands.W_REGISTER, Registers.SETUP_RETR,
                    new[]
                        {
                            (byte) (0x0F << Bits.ARD |
                                    0x0F << Bits.ARC)
                        });

            // Setup, CRC enabled, Power Up, PRX
            SetReceiveMode();
        }

        /// <summary>
        ///   Disables the module
        /// </summary>
        public void Disable()
        {
            _enabled = false;
            SetDisabled();
        }

        /// <summary>
        ///   Enables the module
        /// </summary>
        public void Enable()
        {
            _enabled = true;
            SetEnabled();
        }

        /// <summary>
        ///   Executes a command in NRF24L01+ (for details see module datasheet)
        /// </summary>
        /// <param name = "command">Command</param>
        /// <param name = "addres">Register to write to</param>
        /// <param name = "data">Data to write</param>
        /// <returns>Response byte array. First byte is the status register</returns>
        public byte[] Execute(byte command, byte addres, byte[] data)
        {
            CheckIsInitialized();

            // This command requires module to be in power down or standby mode
            if (command == Commands.W_REGISTER)
                SetDisabled();

            // Create SPI Buffers with Size of Data + 1 (For Command)
            var writeBuffer = new byte[data.Length + 1];
            var readBuffer = new byte[data.Length + 1];

            // Add command and adres to SPI buffer
            writeBuffer[0] = (byte)(command | addres);

            // Add data to SPI buffer
            Array.Copy(data, 0, writeBuffer, 1, data.Length);

            // Do SPI Read/Write
            _spiPort.WriteRead(writeBuffer, readBuffer);

            // Enable module back if it was disabled
            if (command == Commands.W_REGISTER && _enabled)
                SetEnabled();

            // Return ReadBuffer
            return readBuffer;
        }

        /// <summary>
        /// Read 1 of 6 available module addresses
        /// </summary>
        public byte[] GetAddress(AddressSlot slot, int width)
        {
            CheckIsInitialized();
            AddressWidth.Check(width);
            var read = Execute(Commands.R_REGISTER, (byte)slot, new byte[width]);
            var result = new byte[read.Length - 1];
            Array.Copy(read, 1, result, 0, result.Length);

            // Reverse the LSByte to MSByte per nRF24L01+ Datasheet
            result = ByteReverse(result);

            return result;
        }

        /// <summary>
        ///   Reads the current rf channel value set in module
        /// </summary>
        /// <returns></returns>
        public byte GetChannel()
        {
            CheckIsInitialized();

            var result = Execute(Commands.R_REGISTER, Registers.RF_CH, new byte[1]);
            return (byte)(result[1] & 0x7F);
        }

        /// <summary>
        ///   Gets the module radio frequency [MHz]
        /// </summary>
        /// <returns>Frequency in MHz</returns>
        public int GetFrequency()
        {
            return 2400 + GetChannel();
        }

        /// <summary>
        ///   Gets module basic status information
        /// </summary>
        public Status GetStatus()
        {
            CheckIsInitialized();

            var readBuffer = new byte[1];
            _spiPort.WriteRead(new[] { Commands.NOP }, readBuffer);

            return new Status(readBuffer[0]);
        }

        /// <summary>
        ///   Initializes SPI connection and control pins
        /// </summary>
        public void Initialize(SPI.SPI_module spi, Cpu.Pin chipSelectPin, Cpu.Pin chipEnablePin, Cpu.Pin interruptPin)
        {
            // Chip Select : Active Low
            // Clock : Active High, Data clocked in on rising edge
            _spiPort = new SPI(new SPI.Configuration(chipSelectPin, false, 0, 0, false, true, 2000, spi));

            // Initialize IRQ Port
            _irqPin = new InterruptPort(interruptPin, false, Port.ResistorMode.PullUp,
                                        Port.InterruptMode.InterruptEdgeLow);
            _irqPin.OnInterrupt += HandleInterrupt;

            // Initialize Chip Enable Port
            _cePin = new OutputPort(chipEnablePin, false);

            // Module reset time
            Thread.Sleep(100);

            _initialized = true;
        }

        /// <summary>
        ///   Send <param name = "bytes">bytes</param> to given <param name = "address">address</param>
        ///   This is a non blocking method.
        /// </summary>
        public void SendTo(byte[] address, byte[] bytes, Acknowledge acknowledge = Acknowledge.Yes)
        {
            // Address must be LSByte to MSByte per nRF24L01+ Datasheet
            address = ByteReverse(address);

            // Message must be LSByte to MSByte per nRF24L01+ Datasheet
            bytes = ByteReverse(bytes);

            // Chip enable low
            SetDisabled();

            // Setup PTX (Primary TX)
            SetTransmitMode();

            // Write transmit adres to TX_ADDR register.
            Execute(Commands.W_REGISTER, Registers.TX_ADDR, address);

            // Write transmit adres to RX_ADDRESS_P0 (Pipe0) (For Auto ACK)
            Execute(Commands.W_REGISTER, Registers.RX_ADDR_P0, address);

            // Send payload
            Execute(acknowledge == Acknowledge.Yes ? Commands.W_TX_PAYLOAD : Commands.W_TX_PAYLOAD_NO_ACK, 0x00, bytes);

            // Pulse for CE -> starts the transmission.
            SetEnabled();
        }

        /// <summary>
        ///   Sends <param name = "bytes">bytes</param> to given <param name = "address">address</param>
        ///   This is a blocking method that returns true if data was received by the recipient or false if timeout occured.
        /// </summary>
        public bool SendTo(byte[] address, byte[] bytes, int timeout)
        {
            var startTime = DateTime.Now;

            while (true)
            {
                _transmitSuccessFlag.Reset();
                _transmitFailedFlag.Reset();

                SendTo(address, bytes);

                if (WaitHandle.WaitAny(new[] { _transmitSuccessFlag, _transmitFailedFlag }, 200, true) == 0)
                    return true;

                if (DateTime.Now.CompareTo(startTime.AddMilliseconds(timeout)) > 0)
                    return false;

                Debug.Print("Retransmitting packet...");
            }
        }

        /// <summary>
        /// Set one of 6 available module addresses
        /// </summary>
        public void SetAddress(AddressSlot slot, byte[] address)
        {
            CheckIsInitialized();
            AddressWidth.Check(address);

            // Address must be LSByte to MSByte per nRF24L01+ Datasheet
            address = ByteReverse(address);

            Execute(Commands.W_REGISTER, (byte)slot, address);

            if (slot == AddressSlot.Zero)
            {
                _slot0Address = address;
            }
        }

        /// <summary>
        ///   Sets the rf channel value used by all data pipes
        /// </summary>
        /// <param name="channel">7 bit channel value</param>
        public void SetChannel(byte channel)
        {
            CheckIsInitialized();

            var writeBuffer = new[] { (byte)(channel & 0x7F) };
            Execute(Commands.W_REGISTER, Registers.RF_CH, writeBuffer);
        }

        private byte[] ByteReverse(byte[] byteArray)
        {
            //Least significant byte first!
            byte[] buffer = new byte[byteArray.Length];
            for (int i = 0; i < byteArray.Length; i++)
                buffer[i] = byteArray[byteArray.Length - (i + 1)];
            buffer.CopyTo(byteArray, 0);
            return byteArray;
        }

        private void CheckIsInitialized()
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Initialize method needs to be called before this call");
            }
        }

        private void HandleInterrupt(uint data1, uint data2, DateTime dateTime)
        {
            if (!_initialized)
                return;

            if (!_enabled)
            {
                // Flush RX FIFO
                Execute(Commands.FLUSH_RX, 0x00, new byte[0]);
                // Flush TX FIFO
                Execute(Commands.FLUSH_TX, 0x00, new byte[0]);
                return;
            }

            // Disable RX/TX
            SetDisabled();

            // Set PRX
            SetReceiveMode();

            // there are 3 rx pipes in rf module so 3 arrays should be enough to store incoming data
            // sometimes though more than 3 data packets are received somehow
            var payloads = new byte[6][];

            var status = GetStatus();
            byte payloadCount = 0;
            var payloadCorrupted = false;

            OnInterrupt(status);

            if (status.DataReady)
            {
                while (!status.RxEmpty)
                {
                    // Read payload size
                    var payloadLength = Execute(Commands.R_RX_PL_WID, 0x00, new byte[1]);

                    // this indicates corrupted data
                    if (payloadLength[1] > 32)
                    {
                        payloadCorrupted = true;

                        // Flush anything that remains in buffer
                        Execute(Commands.FLUSH_RX, 0x00, new byte[0]);
                    }
                    else
                    {
                        if (payloadCount >= payloads.Length)
                        {
                            Debug.Print("Unexpected payloadCount value = " + payloadCount);
                            Execute(Commands.FLUSH_RX, 0x00, new byte[0]);
                        }
                        else
                        {
                            // Read payload data
                            payloads[payloadCount] = Execute(Commands.R_RX_PAYLOAD, 0x00, new byte[payloadLength[1]]);
                            payloadCount++;
                        }
                    }

                    // Clear RX_DR bit
                    var result = Execute(Commands.W_REGISTER, Registers.STATUS, new[] { (byte)(1 << Bits.RX_DR) });
                    status.Update(result[0]);
                }
            }

            if (status.ResendLimitReached)
            {
                // Flush TX FIFO
                Execute(Commands.FLUSH_TX, 0x00, new byte[0]);

                // Clear MAX_RT bit in status register
                Execute(Commands.W_REGISTER, Registers.STATUS, new[] { (byte)(1 << Bits.MAX_RT) });
            }

            if (status.TxFull)
            {
                // Flush TX FIFO
                Execute(Commands.FLUSH_TX, 0x00, new byte[0]);
            }

            if (status.DataSent)
            {
                // Clear TX_DS bit in status register
                Execute(Commands.W_REGISTER, Registers.STATUS, new[] { (byte)(1 << Bits.TX_DS) });
            }

            // Enable RX
            SetEnabled();

            if (payloadCorrupted)
            {
                Debug.Print("Corrupted data received");
            }
            else if (payloadCount > 0)
            {
                if (payloadCount > payloads.Length)
                    Debug.Print("Unexpected payloadCount value = " + payloadCount);

                for (var i = 0; i < System.Math.Min(payloadCount, payloads.Length); i++)
                {
                    var payload = payloads[i];
                    var payloadWithoutCommand = new byte[payload.Length - 1];
                    Array.Copy(payload, 1, payloadWithoutCommand, 0, payload.Length - 1);
                    OnDataReceived(ByteReverse(payloadWithoutCommand));
                }
            }
            else if (status.DataSent)
            {
                _transmitSuccessFlag.Set();
                OnTransmitSuccess();
            }
            else
            {
                _transmitFailedFlag.Set();
                OnTransmitFailed();
            }
        }

        private void SetDisabled()
        {
            _cePin.Write(false);
            _irqPin.DisableInterrupt();
        }

        private void SetEnabled()
        {
            _irqPin.EnableInterrupt();
            _cePin.Write(true);
        }

        private void SetReceiveMode()
        {
            Execute(Commands.W_REGISTER, Registers.RX_ADDR_P0, _slot0Address);

            Execute(Commands.W_REGISTER, Registers.CONFIG,
                    new[]
                        {
                            (byte) (1 << Bits.PWR_UP |
                                    1 << Bits.CRCO |
                                    1 << Bits.PRIM_RX)
                        });
        }

        private void SetTransmitMode()
        {
            Execute(Commands.W_REGISTER, Registers.CONFIG,
                    new[]
                        {
                            (byte) (1 << Bits.PWR_UP |
                                    1 << Bits.CRCO)
                        });
        }
    }

    #region Supporting Classes & Enums

    public enum Acknowledge { Yes, No }

    public enum AddressSlot
    {
        Zero = Registers.RX_ADDR_P0,
        One = Registers.RX_ADDR_P1,
        Two = Registers.RX_ADDR_P2,
        Three = Registers.RX_ADDR_P3,
        Four = Registers.RX_ADDR_P4,
        Five = Registers.RX_ADDR_P5,
    }

    public enum NRFDataRate { DR1Mbps, DR2Mbps, DR250kbps, }

    public static class AddressWidth
    {
        public const int Max = 5;
        public const int Min = 3;

        public static void Check(byte[] address)
        {
            Check(address.Length);
        }

        public static void Check(int addressWidth)
        {
            if (addressWidth < Min || addressWidth > Max)
            {
                throw new ArgumentException("Address width needs to be 3-5 bytes");
            }
        }

        public static byte Get(byte[] address)
        {
            Check(address);
            return (byte)(address.Length - 2);
        }
    }

    public class Status
    {
        private byte _reg;

        public Status(byte reg)
        {
            _reg = reg;
        }

        public byte DataPipe { get { return (byte)((_reg >> 1) & 7); } }

        public bool DataPipeNotUsed { get { return DataPipe == 6; } }

        public bool DataReady { get { return (_reg & (1 << Bits.RX_DR)) > 0; } }

        public bool DataSent { get { return (_reg & (1 << Bits.TX_DS)) > 0; } }

        public bool ResendLimitReached { get { return (_reg & (1 << Bits.MAX_RT)) > 0; } }

        public bool RxEmpty { get { return DataPipe == 7; } }

        public bool TxFull { get { return (_reg & (1 << Bits.TX_FULL)) > 0; } }

        public override string ToString()
        {
            return "DataReady: " + DataReady +
                   ", DateSent: " + DataSent +
                   ", ResendLimitReached: " + ResendLimitReached +
                   ", TxFull: " + TxFull +
                   ", RxEmpty: " + RxEmpty +
                   ", DataPipe: " + DataPipe +
                   ", DataPipeNotUsed: " + DataPipeNotUsed;
        }

        public void Update(byte reg)
        {
            _reg = reg;
        }
    }

    #endregion Supporting Classes & Enums

    #region nRF24L01+ Register Mappings

    public static class Bits
    {
        public static byte ARC = 0;
        public static byte ARC_CNT = 0;
        public static byte ARD = 4;
        public static byte AW = 0;
        public static byte CONT_WAVE = 7;
        public static byte CRCO = 2;
        public static byte DPL_P0 = 0;
        public static byte DPL_P1 = 1;
        public static byte DPL_P2 = 2;
        public static byte DPL_P3 = 3;
        public static byte DPL_P4 = 4;
        public static byte DPL_P5 = 5;
        public static byte EN_ACK_PAY = 1;
        public static byte EN_CRC = 3;
        public static byte EN_DPL = 2;
        public static byte EN_DYN_ACK;
        public static byte ENAA_P0 = 0;
        public static byte ENAA_P1 = 1;
        public static byte ENAA_P2 = 2;
        public static byte ENAA_P3 = 3;
        public static byte ENAA_P4 = 4;
        public static byte ENAA_P5 = 5;
        public static byte ERX_P0 = 0;
        public static byte ERX_P1 = 1;
        public static byte ERX_P2 = 2;
        public static byte ERX_P3 = 3;
        public static byte ERX_P4 = 4;
        public static byte ERX_P5 = 5;
        public static byte FIFO_FULL = 5;
        public static byte MASK_MAX_RT = 4;
        public static byte MASK_RX_DR = 6;
        public static byte MASK_TX_DS = 5;
        public static byte MAX_RT = 4;
        public static byte PLL_LOCK = 4;
        public static byte PLOS_CNT = 4;
        public static byte PRIM_RX = 0;
        public static byte PWR_UP = 1;
        public static byte RF_DR_HIGH = 3;
        public static byte RF_DR_LOW = 5;
        public static byte RF_PWR = 1;
        public static byte RX_DR = 6;
        public static byte RX_EMPTY = 0;
        public static byte RX_FULL = 1;
        public static byte RX_P_NO = 1;
        public static byte TX_DS = 5;
        public static byte TX_EMPTY = 4;
        public static byte TX_FULL = 0;
        public static byte TX_REUSE = 6;
    }

    public static class Commands
    {
        public const byte FLUSH_RX = 0xE2;
        public const byte FLUSH_TX = 0xE1;
        public const byte NOP = 0xFF;
        public const byte R_REGISTER = 0x00;
        public const byte R_RX_PAYLOAD = 0x61;
        public const byte R_RX_PL_WID = 0x60;
        public const byte REUSE_TX_PL = 0xE3;
        public const byte W_ACK_PAYLOAD = 0xA8;
        public const byte W_REGISTER = 0x20;
        public const byte W_TX_PAYLOAD = 0xA0;
        public const byte W_TX_PAYLOAD_NO_ACK = 0xB0;
    }

    public static class Registers
    {
        public const byte CONFIG = 0x00;
        public const byte DYNPD = 0x1C;
        public const byte EN_AA = 0x01;
        public const byte EN_RXADDR = 0x02;
        public const byte FEATURE = 0x1D;
        public const byte FIFO_STATUS = 0x17;
        public const byte OBSERVE_TX = 0x08;
        public const byte RF_CH = 0x05;
        public const byte RF_SETUP = 0x06;
        public const byte RPD = 0x09;
        public const byte RX_ADDR_P0 = 0x0A;
        public const byte RX_ADDR_P1 = 0x0B;
        public const byte RX_ADDR_P2 = 0x0C;
        public const byte RX_ADDR_P3 = 0x0D;
        public const byte RX_ADDR_P4 = 0x0E;
        public const byte RX_ADDR_P5 = 0x0F;
        public const byte RX_PW_P0 = 0x11;
        public const byte RX_PW_P1 = 0x12;
        public const byte RX_PW_P2 = 0x13;
        public const byte RX_PW_P3 = 0x14;
        public const byte RX_PW_P4 = 0x15;
        public const byte RX_PW_P5 = 0x16;
        public const byte SETUP_AW = 0x03;
        public const byte SETUP_RETR = 0x04;
        public const byte STATUS = 0x07;
        public const byte TX_ADDR = 0x10;
    }

    #endregion nRF24L01+ Register Mappings
}