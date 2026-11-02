# NetToPLCsim transport sources

Upstream: Thomas Wiens, NetToPLCsim, LGPL-3.0-or-later.

Retrieved from the author's SourceForge Subversion repository (HEAD r63) on 2026-10-16:

- https://svn.code.sf.net/p/nettoplcsim/code/trunk/nettoplcsim-s7o/src/IsoOnTcpServer/S7online/PlcS7onlineMsgPump.cs
- https://svn.code.sf.net/p/nettoplcsim/code/trunk/nettoplcsim-s7o/src/IsoOnTcpServer/S7online/PlcS7onlineMsgPumpS7.cs
- https://svn.code.sf.net/p/nettoplcsim/code/trunk/nettoplcsim-s7o/src/IsoOnTcpServer/HexDump.cs
- https://svn.code.sf.net/p/nettoplcsim/code/trunk/nettoplcsim-s7o/src/LICENSE.txt

These files are unmodified and retain their copyright and license headers. Source is distributed alongside the worker and can be rebuilt with build-online.ps1. The proprietary Siemens S7onlinx.dll is NOT copied or redistributed; the worker uses the user's installed runtime.

Only the local S7ONLINE message transport is used. No NetToPLCsim network listener, port 102 takeover, service stopping, or PG/PC configuration changes are performed.
