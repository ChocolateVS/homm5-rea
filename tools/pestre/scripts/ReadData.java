// Prints dwords at addresses as hex and float. Usage: -postScript ReadData.java <outFile> <addr>[:count] ...
import ghidra.app.script.GhidraScript;
import ghidra.program.model.mem.*;
import java.io.*;
public class ReadData extends GhidraScript {
    @Override public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Memory m = currentProgram.getMemory();
        for (int i = 1; i < a.length; i++) {
            String[] p = a[i].split(":");
            int n = p.length > 1 ? Integer.parseInt(p[1]) : 1;
            for (int k = 0; k < n; k++) {
                var ad = toAddr(p[0]).add(k * 4);
                int v = m.getInt(ad);
                out.println(ad + " 0x" + Integer.toHexString(v) + " " + Float.intBitsToFloat(v) + " / dbl? ");
            }
        }
        out.close();
    }
}
