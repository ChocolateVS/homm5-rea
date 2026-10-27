// Prints C strings at addresses. Usage: Strs.java <out> <addr>...
import ghidra.app.script.GhidraScript;
import java.io.*;
public class Strs extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs(); PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        for (int i = 1; i < a.length; i++) { StringBuilder sb = new StringBuilder(); for (int k = 0; k < 200; k++) { byte b = getByte(toAddr(a[i]).add(k)); if (b == 0) break; sb.append((char) b); } out.println(a[i] + " " + sb); }
        out.close();
    }
}
