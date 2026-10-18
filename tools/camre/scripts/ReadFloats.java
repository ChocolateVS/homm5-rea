// Prints 32-bit floats at addresses: -postScript ReadFloats.java <outFile> <addr> <count> [<addr> <count> ...]
import ghidra.app.script.GhidraScript;
import java.io.*;
public class ReadFloats extends GhidraScript {
    @Override public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        for (int i = 1; i + 1 < a.length; i += 2) {
            var addr = toAddr(a[i]); int n = Integer.parseInt(a[i + 1]);
            for (int k = 0; k < n; k++) {
                var p = addr.add(4L * k);
                out.println(p + " " + Float.intBitsToFloat(getInt(p)) + " (0x" + Integer.toHexString(getInt(p)) + ")");
            }
        }
        out.close();
    }
}
