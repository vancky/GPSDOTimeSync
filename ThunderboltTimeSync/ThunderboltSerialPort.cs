﻿using System.Collections.Generic;
using System.IO.Ports;

namespace ThunderboltTimeSync {
	public class ThunderboltPacket {
		/// <summary>
		/// The validity of the packet.
		/// If <code>false</code>, the values of <code>ID</code>, <code>Data</code> and <code>RawData</code> must not be used.
		/// If  <code>true</code>, the packet may still contain invalid values or be of incorrect length.
		/// </summary>
		public bool IsPacketValid { get; }

		/// <summary>
		/// The ID of the packet.
		/// </summary>
		public byte ID { get; }

		/// <summary>
		/// The data contained within the packet.
		/// </summary>
		public List<byte> Data { get; }

		/// <summary>
		/// The raw data of the packet.
		/// Contains the initial [DLE] byte, and the terminating [DLE][ETX] bytes. [DLE] values within the data are still stuffed.
		/// </summary>
		public List<byte> RawData { get; }

		protected ThunderboltPacket(bool isPacketValid, byte id, List<byte> data, List<byte> rawData) {
			IsPacketValid = isPacketValid;

			ID = id;
			Data = data;
			RawData = rawData;
		}
	}

	public class ThunderboltSerialPort {
		private static readonly byte CHAR_DLE = 0x10;
		private static readonly byte CHAR_ETX = 0x03;

		private List<byte> packetBuffer;
		private bool inPacket;

		private SerialPort serialPort;

		/// <summary>
		/// A delegate which is called when a full packet is received over the serial port.
		/// </summary>
		/// <param name="packet">The packet which was received.</param>
		public delegate void PacketReceivedEventHandler(ThunderboltPacket packet);

		/// <summary>
		/// An event which is called when a full packet is received over the serial port.
		/// </summary>
		public event PacketReceivedEventHandler PacketReceived;

		/// <summary>
		/// Creates an instance of the ThunderboltSerialPort class, which processes serial data from a Thunderbolt and 
		/// The serial port passed into the function must not be opened, or have a delegate already attached to the DataReceived event.
		/// </summary>
		/// <param name="serialPort">The serial port on which to communicate with the Thunderbolt.</param>
		public ThunderboltSerialPort(SerialPort serialPort) {
			packetBuffer = new List<byte>();
			inPacket = false;

			this.serialPort = serialPort;
			serialPort.DataReceived += DataReceived;
		}

		/// <summary>
		/// Begins processing serial data.
		/// </summary>
		public void Open() {
			serialPort.Open();
		}

		/// <summary>
		/// Converts a stuffed byte list (one where all 0x10 bytes are replaced with 0x10 0x10) into an unstuffed byte list.
		/// </summary>
		/// <param name="data">A reference to the list containing the data to be unstuffed.</param>
		/// <returns>True if the unstuffing was successful, false otherwise.</returns>
		private bool Unstuff(ref List<byte> data) {
			List<byte> newData = new List<byte>();

			bool inStuffedDLE = false;
			foreach (byte b in data) {
				if (b == CHAR_DLE) {
					if (!inStuffedDLE) {
						newData.Add(b);
						inStuffedDLE = true;
					} else {
						// If we see a DLE after we've already seen one (inStuffedDLE == true), we don't need to add the byte to the list because we already did when the first byte was encountered
						// Just set the flag to false so that if this stuffed DLE is immediately followed by another, it will be correctly parsed
						inStuffedDLE = false;
					}
				} else {
					if (inStuffedDLE) {
						return false;
					}

					newData.Add(b);
				}
			}

			data = newData;

			return true;
		}

		private void ProcessPacket() {
			byte id = packetBuffer[1];

			// Grab only the data - not the first [DLE]<id> or the last [DLE][ETX]
			List<byte> data = packetBuffer.GetRange(2, packetBuffer.Count - 4);
			bool isPacketValid = Unstuff(ref data);

			ThunderboltPacket packet;

			if (isPacketValid) {
				packet = new ThunderboltPacket(isPacketValid, id, data, packetBuffer);
			} else {
				packet = new ThunderboltPacket(isPacketValid, 0, new List<byte>(), new List<byte>());
			}

			PacketReceived?.Invoke(packet);
		}

		private void DataReceived(object sender, SerialDataReceivedEventArgs e) {
			int possibleCurrentByte;

			while ((possibleCurrentByte = serialPort.ReadByte()) != -1) {
				// Once we're sure the byte that was read wasn't -1 (which signifies the end of the read), we're safe to cast to a byte
				byte currentByte = (byte) possibleCurrentByte;

				if (inPacket) {
					packetBuffer.Add(currentByte);

					// Check buffer length to ensure we've reached a plausible end of packet.
					// 5 bytes is [DLE]<id><1 byte of data>[DLE][ETX]
					if (currentByte == CHAR_ETX && packetBuffer.Count >= 5) {
						int numberOfPrecedingDLEs = 0;

						// Count number of DLEs, excluding the first two bytes (initial DLE and id)
						for (int i = 2; i < packetBuffer.Count; ++i) {
							if (packetBuffer[i] == CHAR_DLE) {
								++numberOfPrecedingDLEs;
							}
						}

						// Odd number of DLEs means the ETX does in fact signify the end of the packet
						if (numberOfPrecedingDLEs % 2 == 1) {
							ProcessPacket();

							packetBuffer.Clear();
							inPacket = false;
						}
					}
				} else {
					// A DLE received when not currently in a packet signifies the beginning of a packet
					if (currentByte == CHAR_DLE) {
						packetBuffer.Add(currentByte);

						inPacket = true;
					}
				}
			}
		}
	}
}