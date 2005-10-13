//
// Mono.Unix/UnixMarshal.cs
//
// Authors:
//   Jonathan Pryor (jonpryor@vt.edu)
//
// (C) 2004 Jonathan Pryor
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using Mono.Unix;

namespace Mono.Unix {

	// Scenario:  We want to be able to translate an Error to a string.
	//  Problem:  Thread-safety.  Strerror(3) isn't thread safe (unless
	//            thread-local-variables are used, which is probably only 
	//            true on Windows).
	// Solution:  Use strerror_r().
	//  Problem:  strerror_r() isn't portable. 
	//            (Apparently Solaris doesn't provide it.)
	// Solution:  Cry.  Then introduce an intermediary, ErrorMarshal.
	//            ErrorMarshal exposes a single public delegate, Translator,
	//            which will convert an Error to a string.  It's static
	//            constructor first tries using strerror_r().  If it works,
	//            great; use it in the future.  If it doesn't work, fallback to
	//            using strerror(3).
	//            This should be thread safe, since the check is done within the
	//            class constructor lock.
	//            Strerror(3) will be thread-safe from managed code, but won't
	//            be thread-safe between managed & unmanaged code.
	internal class ErrorMarshal
	{
		internal delegate string ErrorTranslator (Error errno);

		internal static readonly ErrorTranslator Translate;

		static ErrorMarshal ()
		{
			try {
				Translate = new ErrorTranslator (strerror_r);
				Translate (Error.ERANGE);
			}
			catch (EntryPointNotFoundException e) {
				Translate = new ErrorTranslator (strerror);
			}
		}

		private static string strerror (Error errno)
		{
			return Stdlib.strerror (errno);
		}

		private static string strerror_r (Error errno)
		{
			StringBuilder buf = new StringBuilder (16);
			int r = 0;
			do {
				buf.Capacity *= 2;
				r = Syscall.strerror_r (errno, buf);
			} while (r == -1 && Stdlib.GetLastError() == Error.ERANGE);

			if (r == -1)
				return "** Unknown error code: " + ((int) errno) + "**";
			return buf.ToString();
		}
	}

	public sealed /* static */ class UnixMarshal
	{
		private UnixMarshal () {}

		[Obsolete ("Use GetErrorDescription (Mono.Unix.Native.Errno)")]
		public static string GetErrorDescription (Error errno)
		{
			return ErrorMarshal.Translate (errno);
		}

		[CLSCompliant (false)]
		public static string GetErrorDescription (Native.Errno errno)
		{
			return ErrorMarshal.Translate ((Error) (int) errno);
		}

		public static IntPtr Alloc (long size)
		{
			if (size < 0)
				throw new ArgumentOutOfRangeException ("size", "< 0");
			return Stdlib.malloc ((ulong) size);
		}

		public static IntPtr ReAlloc (IntPtr ptr, long size)
		{
			if (size < 0)
				throw new ArgumentOutOfRangeException ("size", "< 0");
			return Stdlib.realloc (ptr, (ulong) size);
		}

		public static void Free (IntPtr ptr)
		{
			Stdlib.free (ptr);
		}

		public static string PtrToString (IntPtr p)
		{
			if (p == IntPtr.Zero)
				return null;
			return PtrToString (p, Encoding.Default);
		}

		public static string PtrToString (IntPtr p, Encoding encoding)
		{
			if (p == IntPtr.Zero)
				return null;

			int len = GetStringByteLength (p, encoding);
			byte[] string_buf = new byte [len];
			Marshal.Copy (p, string_buf, 0, string_buf.Length);
			return encoding.GetString (string_buf);
		}

		private static int GetStringByteLength (IntPtr p, Encoding encoding)
		{
			Type encodingType = encoding.GetType ();

			int len = -1;

			// Encodings that will always end with a null byte
			if (encodingType == typeof(UTF8Encoding) ||
					encodingType == typeof(UTF7Encoding) ||
					encodingType == typeof(ASCIIEncoding)) {
				len = checked ((int) Native.Stdlib.strlen (p));
			}
			// Encodings that will always end with a 0x0000 16-bit word
			else if (encodingType == typeof(UnicodeEncoding)) {
				len = GetInt16BufferLength (p);
			}
			// Some non-public encoding, such as Latin1 or a DBCS charset.
			// Look for a sequence of encoding.GetMaxByteCount() bytes that are all
			// 0, which should be the terminating null.
			// This is "iffy", since it may fail for variable-width encodings; for
			// example, UTF8Encoding.GetMaxByteCount(1) = 4, so this would read 3
			// bytes past the end of the string, possibly into garbage memory
			// (which is why we special case UTF above).
			else {
				len = GetRandomBufferLength (p, encoding.GetMaxByteCount(1));
			}

			if (len == -1)
				throw new NotSupportedException ("Unable to determine native string buffer length");
			return len;
		}

		private static int GetInt16BufferLength (IntPtr p)
		{
			int len = 0;
			while (Marshal.ReadInt16 (p, len) != 0)
				checked {++len;}
			return len;
		}

		private static int GetInt32BufferLength (IntPtr p)
		{
			int len = 0;
			while (Marshal.ReadInt32 (p, len) != 0)
				checked {++len;}
			return len;
		}

		private static int GetRandomBufferLength (IntPtr p, int nullLength)
		{
			switch (nullLength) {
				case 1: return checked ((int) Native.Stdlib.strlen (p));
				case 2: return GetInt16BufferLength (p);
				case 4: return GetInt32BufferLength (p);
			}

			int len = 0;
			int num_null_seen = 0;

			do {
				byte b = Marshal.ReadByte (p, len++);
				if (b == 0)
					++num_null_seen;
				else
					num_null_seen = 0;
			} while (num_null_seen != nullLength);

			return len;
		}

		/*
		 * Marshal a C `char **'.  ANSI C `main' requirements are assumed:
		 *
		 *   stringArray is an array of pointers to C strings
		 *   stringArray has a terminating NULL string.
		 *
		 * For example:
		 *   stringArray[0] = "string 1";
		 *   stringArray[1] = "string 2";
		 *   stringArray[2] = NULL
		 *
		 * The terminating NULL is required so that we know when to stop looking
		 * for strings.
		 */
		public static string[] PtrToStringArray (IntPtr stringArray)
		{
			return PtrToStringArray (stringArray, Encoding.Default);
		}

		public static string[] PtrToStringArray (IntPtr stringArray, Encoding encoding)
		{
			if (stringArray == IntPtr.Zero)
				return new string[]{};

			int argc = CountStrings (stringArray);
			return PtrToStringArray (argc, stringArray, encoding);
		}

		private static int CountStrings (IntPtr stringArray)
		{
			int count = 0;
			while (Marshal.ReadIntPtr (stringArray, count*IntPtr.Size) != IntPtr.Zero)
				++count;
			return count;
		}

		/*
		 * Like PtrToStringArray(IntPtr), but it allows the user to specify how
		 * many strings to look for in the array.  As such, the requirement for a
		 * terminating NULL element is not required.
		 *
		 * Usage is similar to ANSI C `main': count is argc, stringArray is argv.
		 * stringArray[count] is NOT accessed (though ANSI C requires that 
		 * argv[argc] = NULL, which PtrToStringArray(IntPtr) requires).
		 */
		public static string[] PtrToStringArray (int count, IntPtr stringArray)
		{
			return PtrToStringArray (count, stringArray, Encoding.Default);
		}

		public static string[] PtrToStringArray (int count, IntPtr stringArray, Encoding encoding)
		{
			if (count < 0)
				throw new ArgumentOutOfRangeException ("count", "< 0");
			if (stringArray == IntPtr.Zero)
				return new string[count];

			string[] members = new string[count];
			for (int i = 0; i < count; ++i) {
				IntPtr s = Marshal.ReadIntPtr (stringArray, i * IntPtr.Size);
				members[i] = PtrToString (s, encoding);
			}

			return members;
		}

		public static IntPtr StringToAlloc (string s)
		{
			return StringToAlloc (s, Encoding.Default);
		}

		public static IntPtr StringToAlloc (string s, Encoding encoding)
		{
			return StringToAlloc (s, 0, s.Length, encoding);
		}

		public static IntPtr StringToAlloc (string s, int index, int count)
		{
			return StringToAlloc (s, index, count, Encoding.Default);
		}

		public static IntPtr StringToAlloc (string s, int index, int count, Encoding encoding)
		{
			int min_byte_count = encoding.GetMaxByteCount(1);
			char[] copy = s.ToCharArray (index, count);
			byte[] marshal = new byte [encoding.GetByteCount (copy) + min_byte_count];

			int bytes_copied = encoding.GetBytes (copy, 0, copy.Length, marshal, 0);

			if (bytes_copied != (marshal.Length-min_byte_count))
				throw new NotSupportedException ("encoding.GetBytes() doesn't equal encoding.GetByteCount()!");

			IntPtr mem = Alloc (marshal.Length);
			if (mem == IntPtr.Zero)
				throw new OutOfMemoryException ();

			bool copied = false;
			try {
				Marshal.Copy (marshal, 0, mem, marshal.Length);
				copied = true;
			}
			finally {
				if (!copied)
					Free (mem);
			}

			return mem;
		}

		public static bool ShouldRetrySyscall (int r)
		{
			if (r == -1 && Stdlib.GetLastError () == Error.EINTR)
				return true;
			return false;
		}

		[Obsolete ("Use ShouldRetrySyscall (int, out Mono.Unix.Native.Errno")]
		public static bool ShouldRetrySyscall (int r, out Error error)
		{
			error = (Error) 0;
			if (r == -1 && (error = Stdlib.GetLastError ()) == Error.EINTR)
				return true;
			return false;
		}

		[CLSCompliant (false)]
		public static bool ShouldRetrySyscall (int r, out Native.Errno error)
		{
			error = (Native.Errno) 0;
			if (r == -1 && (error = Native.Stdlib.GetLastError ()) == Native.Errno.EINTR)
				return true;
			return false;
		}

		// we can't permit any printf(3)-style formatting information, since that
		// would kill the stack.  However, replacing %% is silly, and some %* are
		// permitted (such as %m in syslog to print strerror(errno)).
		internal static string EscapeFormatString (string message, 
				char [] permitted)
		{
			if (message == null)
				return "";
			StringBuilder sb = new StringBuilder (message.Length);
			for (int i = 0; i < message.Length; ++i) {
				char c = message [i];
				sb.Append (c);
				if (c == '%' && (i+1) < message.Length) {
					char n = message [i+1];
					if (n == '%' || IsCharPresent (permitted, n))
						sb.Append (n);
					else
						sb.Append ('%').Append (n);
					++i;
				}
				// invalid format string: % at EOS.
				else if (c == '%')
					sb.Append ('%');
			}
			return sb.ToString ();
		}

		private static bool IsCharPresent (char[] array, char c)
		{
			if (array == null)
				return false;
			for (int i = 0; i < array.Length; ++i)
				if (array [i] == c)
					return true;
			return false;
		}

		[Obsolete ("Use CreateExceptionForError (Mono.Unix.Native.Errno)")]
		internal static Exception CreateExceptionForError (Error errno)
		{
			string message = GetErrorDescription (errno);
			UnixIOException p = new UnixIOException (errno);
			switch (errno) {
				case Error.EFAULT:        return new NullReferenceException (message, p);
				case Error.EINVAL:        return new ArgumentException (message, p);
				case Error.EIO:
				  case Error.ENOSPC:
				  case Error.EROFS:
				  case Error.ESPIPE:
					return new IOException (message, p);
				case Error.ENAMETOOLONG:  return new PathTooLongException (message, p);
				case Error.ENOENT:        return new FileNotFoundException (message, p);
				case Error.ENOEXEC:       return new InvalidProgramException (message, p);
				case Error.EOVERFLOW:     return new OverflowException (message, p);
				case Error.ERANGE:        return new ArgumentOutOfRangeException (message);
				default: /* ignore */     break;
			}
			return p;
		}

		internal static Exception CreateExceptionForError (Native.Errno errno)
		{
			string message = GetErrorDescription (errno);
			UnixIOException p = new UnixIOException (errno);
			switch (errno) {
				case Native.Errno.EFAULT:        return new NullReferenceException (message, p);
				case Native.Errno.EINVAL:        return new ArgumentException (message, p);
				case Native.Errno.EIO:
				  case Native.Errno.ENOSPC:
				  case Native.Errno.EROFS:
				  case Native.Errno.ESPIPE:
					return new IOException (message, p);
				case Native.Errno.ENAMETOOLONG:  return new PathTooLongException (message, p);
				case Native.Errno.ENOENT:        return new FileNotFoundException (message, p);
				case Native.Errno.ENOEXEC:       return new InvalidProgramException (message, p);
				case Native.Errno.EOVERFLOW:     return new OverflowException (message, p);
				case Native.Errno.ERANGE:        return new ArgumentOutOfRangeException (message);
				default: /* ignore */     break;
			}
			return p;
		}

		internal static Exception CreateExceptionForLastError ()
		{
			return CreateExceptionForError (Stdlib.GetLastError());
		}

		[Obsolete ("Use ThrowExceptionForError (Mono.Unix.Native.Errno)")]
		public static void ThrowExceptionForError (Error errno)
		{
			throw CreateExceptionForError (errno);
		}

		[CLSCompliant (false)]
		public static void ThrowExceptionForError (Native.Errno errno)
		{
			throw CreateExceptionForError (errno);
		}

		public static void ThrowExceptionForLastError ()
		{
			throw CreateExceptionForLastError ();
		}

		[Obsolete ("Use ThrowExceptionForErrorIf (int, Mono.Unix.Native.Errno)")]
		public static void ThrowExceptionForErrorIf (int retval, Error errno)
		{
			if (retval == -1)
				ThrowExceptionForError (errno);
		}

		[CLSCompliant (false)]
		public static void ThrowExceptionForErrorIf (int retval, Native.Errno errno)
		{
			if (retval == -1)
				ThrowExceptionForError (errno);
		}

		public static void ThrowExceptionForLastErrorIf (int retval)
		{
			if (retval == -1)
				ThrowExceptionForLastError ();
		}
	}
}

// vim: noexpandtab
