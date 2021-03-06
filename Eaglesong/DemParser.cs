﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dota2;
using Newtonsoft.Json.Converters;
using ProtoBuf.Meta;

namespace Eaglesong
{
    public class DemParser
    {
        private const ulong CompressedKindMask = 0x70;

        private readonly Dictionary<ParserPhase, LinkedList<object>> _messages = new Dictionary<ParserPhase, LinkedList<object>>();

        private readonly OrderedDictionary<StringTable> _stringTables = new OrderedDictionary<StringTable>();

        private readonly List<dota2.CDOTAModifierBuffTableEntry> _activeModfierEntries = new List<CDOTAModifierBuffTableEntry>(); 

        public ParserPhase Phase { get; private set; }

        public Dictionary<ParserPhase, LinkedList<object>> Read(string fileName)
        {
            using (var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
            {
                var buf = new byte[8];
                fs.Read(buf, 0, 8);
                if (System.Text.Encoding.UTF8.GetString(buf) != "PBUFDEM\0")
                {
                    throw new InvalidDataException("Invalid Header");
                }

                buf = new byte[4];
                fs.Read(buf, 0, 4);
                if (BitConverter.IsLittleEndian != true) // the bit converter requires the bytes in the computer's endian-ness
                {
                    Array.Reverse(buf);
                }
                //int summaryOffset = BitConverter.ToInt32(buf, 0);

                this.Phase = ParserPhase.Prologue;
                this._messages[this.Phase] = new LinkedList<object>();

                while (fs.Position < fs.Length)
                {
                    object msg = DemParser.ParseMessage(fs);

                    // special message handling
                    if (msg is dota2.CDemoSyncTick)
                    {
                        this.Phase = ParserPhase.Match;
                        this._messages[this.Phase] = new LinkedList<object>();
                    }
                    else if (msg is dota2.CDemoStop)
                    {
                        this.Phase = ParserPhase.Epilogue;
                        this._messages[this.Phase] = new LinkedList<object>();
                    }
                    else if (msg is dota2.IBaseWithEmbedded)
                    {
                        var msgBase = msg as dota2.IBaseWithEmbedded;
                        foreach (object inner in msgBase.EmbeddedMessages)
                        {
                            StringTable t;
                            if (inner is dota2.CSVCMsg_CreateStringTable)
                            {
                                t = new StringTable((dota2.CSVCMsg_CreateStringTable)inner);
                                this._stringTables.Add(t.Name, t);
                                this.HandleTable(t);
                            }
                            else if (inner is dota2.CSVCMsg_UpdateStringTable)
                            {
                                var ust = (dota2.CSVCMsg_UpdateStringTable)inner;
                                t = this._stringTables[ust.table_id];
                                t.Update(ust);
                                this.HandleTable(t);
                            }
                        }
                    }

                    this._messages[this.Phase].AddLast(msg);
                }
            }

            return this._messages;
        }

        /// <summary>
        /// Handles processing for specific tables
        /// </summary>
        /// <param name="table"></param>
        private void HandleTable(StringTable table)
        {
            IEnumerable<KeyValuePair<int, StringTableRow>> lazyRowList = table.Rows.Where(kvp => (kvp.Value.Value != null && kvp.Value.Value.Length > 0));
            List<KeyValuePair<int, StringTableRow>> rows; // the linq will lazy evaluate, but if we're modifying the rows then we will need to force the full evaluation

            string name = table.Name.ToLower();
            switch (name)
            {
                case "activemodifiers":
                    rows = lazyRowList.ToList();
                    foreach (KeyValuePair<int, StringTableRow> kvp in rows)
                    {
                        var entry = (dota2.CDOTAModifierBuffTableEntry) RuntimeTypeModel.Default.Deserialize(new MemoryStream(kvp.Value.Value), null, typeof (dota2.CDOTAModifierBuffTableEntry));
                        table.Rows[kvp.Key] = new ActiveModifierRow(kvp.Value, entry);
                        this._activeModfierEntries.Add(entry);
                    }
                    break;

                case "userinfo":
                    rows = lazyRowList.ToList();
                    foreach (KeyValuePair<int, StringTableRow> kvp in rows)
                    {
                        table.Rows[kvp.Key] = new UserInfoRow(kvp.Value, UserInfo.ParseUserInfo(kvp.Value.Value));
                    }
                    break;

                case "instancebaseline":
                    // TODO
                    break;
            }
        }

#region TypeMaps
        private static readonly Dictionary<ulong, Type> BaseTypeMap = new Dictionary<ulong, Type>
        {
            {0, typeof(dota2.CDemoStop)},
            {1, typeof(dota2.CDemoFileHeader)},
            {2, typeof(dota2.CDemoFileInfo)},
            {3, typeof(dota2.CDemoSyncTick)},
            {4, typeof(dota2.CDemoSendTables)},
            {5, typeof(dota2.CDemoClassInfo)},
            {6, typeof(dota2.CDemoStringTables)},
            {7, typeof(dota2.CDemoPacket)},
            {8, typeof(dota2.CDemoSignonPacket)},
            {9, typeof(dota2.CDemoConsoleCmd)},
            {10, typeof(dota2.CDemoCustomData)},
            {11, typeof(dota2.CDemoCustomDataCallbacks)},
            {12, typeof(dota2.CDemoUserCmd)},
            {13, typeof(dota2.CDemoFullPacket)},
            {14, typeof(dota2.CDemoSaveGame)}
        };
        private static readonly Dictionary<ulong, Type> EmbeddedTypeMap = new Dictionary<ulong, Type>
        {
            {4, typeof(dota2.CNETMsg_Tick)},
            {6, typeof(dota2.CNETMsg_SetConVar)},
            {7, typeof(dota2.CNETMsg_SignonState)},
            {8, typeof(dota2.CSVCMsg_ServerInfo)},
            {9, typeof(dota2.CSVCMsg_SendTable)},
            {10, typeof(dota2.CSVCMsg_ClassInfo)},
            {12, typeof(dota2.CSVCMsg_CreateStringTable)},
            {13, typeof(dota2.CSVCMsg_UpdateStringTable)},
            {14, typeof(dota2.CSVCMsg_VoiceInit)},
            {15, typeof(dota2.CSVCMsg_VoiceData)},
            {17, typeof(dota2.CSVCMsg_Sounds)},
            {18, typeof(dota2.CSVCMsg_SetView)},
            {23, typeof(dota2.CSVCMsg_UserMessage)},
            {24, typeof(dota2.EDotaEntityMessages)},
            {25, typeof(dota2.CSVCMsg_GameEvent)},
            {26, typeof(dota2.CSVCMsg_PacketEntities)},
            {27, typeof(dota2.CSVCMsg_TempEntities)},
            {30, typeof(dota2.CSVCMsg_GameEventList)}
        };
#endregion

        /// <summary>
        /// Parses a base message and its embedded message (if required)
        /// </summary>
        /// <param name="fs"></param>
        /// <returns></returns>
        public static object ParseMessage(Stream fs)
        {
            ulong kind = DemParser.ReadVarInt(fs);
            ulong tick = DemParser.ReadVarInt(fs);
            ulong size = DemParser.ReadVarInt(fs);
            var buf = new byte[size];
            fs.Read(buf, 0, (int)size);

            // decompress if needed
            bool isCompressed = (kind & DemParser.CompressedKindMask) != 0;
            if (isCompressed)
            {
                kind = kind - DemParser.CompressedKindMask;
                buf = Snappy.SnappyCodec.Uncompress(buf);
            }

            object message = RuntimeTypeModel.Default.Deserialize(new MemoryStream(buf), null, DemParser.BaseTypeMap[kind]);
            DemParser.PrintMessage(message, kind, tick, size, buf, isCompressed);
            return message;
        }

        /// <summary>
        /// Parses the embedded message from within a base message
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static object[] ParseEmbeddedMessages(byte[] data)
        {
            var fs = new MemoryStream(data);
            var messages = new LinkedList<object>();

            while (fs.Position < fs.Length)
            {
                ulong kind = DemParser.ReadVarInt(fs);
                ulong size = DemParser.ReadVarInt(fs);
                var buf = new byte[size];
                fs.Read(buf, 0, (int)size);

                object message = RuntimeTypeModel.Default.Deserialize(new MemoryStream(buf), null, DemParser.EmbeddedTypeMap[kind]);

                messages.AddLast(message);
            }

            var array = new object[messages.Count];
            messages.CopyTo(array, 0);
            return array;
        }

        /// <summary>
        /// Reads in a VarInt
        /// </summary>
        /// <param name="fs"></param>
        /// <returns></returns>
        public static ulong ReadVarInt(Stream fs)
        {
            ulong result = 0;
            var shift = 0;

            const int max = sizeof(long) * 8;
            while (shift < max)
            {
                var b = (byte)fs.ReadByte();
                var temp = (ulong)(b & 0x7f);
                result |= temp << shift;

                if ((b & 0x80) != 0x80)
                {
                    return result;
                }

                shift += 7;
            }

            throw new InvalidDataException("Invalid VarInt.");
        }


        private static void PrintMessage(object message, ulong kind, ulong tick, ulong size, byte[] buf, bool isCompressed)
        {
#if DEBUG
            Console.WriteLine("==== #{0}: Tick:{1} '{2}' Size:{3} UncompressedSize:{4} ====", Messages.Count, tick, BaseTypeMap[kind], size, isCompressed ? buf.Length : 0);
            switch (kind)
            {
                case 1:
                    {
                        Console.WriteLine("---- {0} ({1} bytes) -----------------", BaseTypeMap[kind], buf.Length);
                        dota2.CDemoFileHeader msg = (dota2.CDemoFileHeader)message;
                        Console.WriteLine("demo_file_stamp: \"{0}\"", msg.demo_file_stamp);
                        Console.WriteLine("network_protocol: {0}", msg.network_protocol);
                        Console.WriteLine("server_name: \"{0}\"", msg.server_name);
                        Console.WriteLine("client_name: \"{0}\"", msg.client_name);
                        Console.WriteLine("map_name: \"{0}\"", msg.map_name);
                        Console.WriteLine("game_directory: \"{0}\"", msg.game_directory);
                        Console.WriteLine("fullpackets_version: {0}", msg.fullpackets_version);
                        Console.WriteLine("allow_clientside_entities: {0}", msg.allow_clientside_entities);
                    }
                    break;

                case 4:
                    {
                        dota2.CDemoSendTables msg = (dota2.CDemoSendTables)message;
                        PrintEmbeddedMessage(msg);
                    }
                    break;

                case 5:
                    {
                        Console.WriteLine("---- {0} ({1} bytes) -----------------", BaseTypeMap[kind], buf.Length);
                        dota2.CDemoClassInfo msg = (dota2.CDemoClassInfo)message;
                        foreach (dota2.CDemoClassInfo.class_t cInfo in msg.classes)
                        {
                            Console.WriteLine("classes {");
                            Console.WriteLine("  class_id: {0}", cInfo.class_id);
                            Console.WriteLine("  network_name: \"{0}\"", cInfo.network_name);
                            Console.WriteLine("  table_name: \"{0}\"", cInfo.table_name);
                            Console.WriteLine("}");
                        }
                    }
                    break;

                case 6:
                    {
                        Console.WriteLine("---- {0} ({1} bytes) -----------------", BaseTypeMap[kind], buf.Length);
                        dota2.CDemoStringTables msg = (dota2.CDemoStringTables)message;
                        int i = 0;
                        foreach (dota2.CDemoStringTables.table_t table in msg.tables)
                        {
                            Console.WriteLine("#{0} {1} flags:{2} ({3} items) {4} bytes", i++, table.table_name, table.table_flags.ToString("X4"), table.items.Count, -1);
                            int j = 0;
                            foreach (dota2.CDemoStringTables.items_t item in table.items)
                            {
                                if (item.data == null)
                                {
                                    item.data = new byte[0];
                                }
                                Console.WriteLine("    #{0} '{1}' ({2} bytes)", j++, item.str, item.data.Length); 
                            }
                        }
                    }
                    break;

                case 8:
                    {
                        dota2.CDemoSignonPacket msg = (dota2.CDemoSignonPacket)message;
                        PrintEmbeddedMessage(msg);
                    }
                    break;
            }
#endif
        }

        private void PrintEmbeddedMessage(dota2.IBaseWithEmbedded message)
        {
#if DEBUG
            foreach (object embedded in message.EmbeddedMessages)
            {
                if (embedded is dota2.CSVCMsg_ServerInfo)
                {
                    Console.WriteLine("---- {0} ({1} bytes) -----------------", typeof(dota2.CSVCMsg_ServerInfo), -1);
                    dota2.CSVCMsg_ServerInfo msg = (dota2.CSVCMsg_ServerInfo)embedded;
                    Console.WriteLine("protocol: {0}", msg.protocol);
                    Console.WriteLine("server_count: {0}", msg.server_count);
                    Console.WriteLine("is_dedicated: {0}", msg.is_dedicated);
                    Console.WriteLine("is_hltv: {0}", msg.is_hltv);
                    Console.WriteLine("c_os: {0}", msg.c_os);
                    Console.WriteLine("map_crc: {0}", msg.map_crc);
                    Console.WriteLine("client_crc: {0}", msg.client_crc);
                    Console.WriteLine("string_table_crc: {0}", msg.string_table_crc);
                    Console.WriteLine("max_clients: {0}", msg.max_clients);
                    Console.WriteLine("max_classes: {0}", msg.max_classes);
                    Console.WriteLine("player_slot: {0}", msg.player_slot);
                    Console.WriteLine("tick_interval: {0}", msg.tick_interval);
                    Console.WriteLine("game_dir: \"{0}\"", msg.game_dir);
                    Console.WriteLine("map_name: \"{0}\"", msg.map_name);
                    Console.WriteLine("sky_name: \"{0}\"", msg.sky_name);
                    Console.WriteLine("host_name: \"{0}\"", msg.host_name);
                }
                else if (embedded is dota2.CNETMsg_Tick)
                {
                    Console.WriteLine("---- {0} ({1} bytes) -----------------", typeof(dota2.CNETMsg_Tick), -1);
                    dota2.CNETMsg_Tick msg = (dota2.CNETMsg_Tick)embedded;
                    Console.WriteLine("tick: {0}", msg.tick);
                }
                else if (embedded is dota2.CNETMsg_SetConVar)
                {
                    Console.WriteLine("---- {0} ({1} bytes) -----------------", typeof(dota2.CNETMsg_SetConVar), -1);
                    dota2.CNETMsg_SetConVar msg = (dota2.CNETMsg_SetConVar)embedded;
                    Console.WriteLine("convars {");
                    foreach (dota2.CMsg_CVars.CVar cvar in msg.convars.cvars)
                    {
                        Console.WriteLine("  cvars {");
                        Console.WriteLine("    name: \"{0}\"", cvar.name);
                        Console.WriteLine("    value: \"{0}\"", cvar.value);
                        Console.WriteLine("  }");
                    }
                    Console.WriteLine("}");
                }
                else if (embedded is dota2.CSVCMsg_CreateStringTable)
                {
                    Console.WriteLine("---- {0} ({1} bytes) -----------------", typeof(dota2.CSVCMsg_CreateStringTable), -1);
                    dota2.CSVCMsg_CreateStringTable msg = (dota2.CSVCMsg_CreateStringTable)embedded;
                    Console.WriteLine("name: \"{0}\"", msg.name);
                    Console.WriteLine("max_entries: {0}", msg.max_entries);
                    Console.WriteLine("num_entries: {0}", msg.num_entries);
                    Console.WriteLine("user_data_fixed_size: {0}", msg.user_data_fixed_size);
                    Console.WriteLine("user_data_size: {0}", msg.user_data_size);
                    Console.WriteLine("user_data_size_bits: {0}", msg.user_data_size_bits);
                    Console.WriteLine("flags: {0}", msg.flags);
                }
                else if (embedded is dota2.CNETMsg_SignonState)
                {
                    Console.WriteLine("---- {0} ({1} bytes) -----------------", typeof(dota2.CNETMsg_SignonState), -1);
                    dota2.CNETMsg_SignonState msg = (dota2.CNETMsg_SignonState)embedded;
                    Console.WriteLine("signon_state: {0}", msg.signon_state);
                    Console.WriteLine("spawn_count: {0}", msg.spawn_count);
                    Console.WriteLine("num_server_players: {0}", msg.num_server_players);
                }
                else if (embedded is dota2.CSVCMsg_SendTable)
                {
                    Console.WriteLine("---- {0} ({1} bytes) -----------------", typeof(dota2.CSVCMsg_SendTable), -1);
                    dota2.CSVCMsg_SendTable msg = (dota2.CSVCMsg_SendTable)embedded;
                    Console.WriteLine("net_table_name: \"{0}\" ({1} items)", msg.net_table_name, msg.props.Count);
                }
            }
#endif
        }
    }
}
