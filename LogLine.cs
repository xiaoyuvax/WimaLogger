using System;
using System.Collections.Generic;
using System.Text;

namespace Wima.Log
{
   public record LogLine(long id, DateTime logTime, string logLevel, string logMsg, string verBoseMsg, string stackTrace = null);
}
