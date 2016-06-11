using System.IO;
using System.Text;

namespace StreamJsonRpc
{
    internal class SplitJoinStreamOptions
    {
        public Stream Readable
        {
            get;
            set;
        }

        public Stream Writable
        {
            get;
            set;
        }

        public string Delimiter
        {
            get;
            set;
        }

        public Encoding Encoding
        {
            get;
            set;
        }

        public bool LeaveOpen
        {
            get;
            set;
        }

        public bool ReadTrailing
        {
            get;
            set;
        }
    }
}
