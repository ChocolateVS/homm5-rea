// Follows Nival XML reader functions: prints every field ("FieldName" call) with its offset
// expression, descending into struct readers (directly or through one wrapper level).
// Usage: -postScript ReaderChain.java <outFile> <addr> [addr...]
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import java.io.*;
import java.util.*;
import java.util.regex.*;

public class ReaderChain extends GhidraScript {
    DecompInterface di;
    PrintWriter out;
    Map<Long, String> cache = new HashMap<>();

    String decomp(long a) {
        if (cache.containsKey(a)) return cache.get(a);
        Function f = currentProgram.getListing().getFunctionAt(toAddr(a));
        String c = "";
        if (f != null) {
            DecompileResults r = di.decompileFunction(f, 60, monitor);
            if (r.decompileCompleted()) c = r.getDecompiledFunction().getC();
        }
        cache.put(a, c);
        return c;
    }

    static final Pattern FIELD = Pattern.compile("(FUN_[0-9a-f]+)\\([A-Za-z_0-9]+,\"([^\"]+)\",0,([^,]+),");
    static final Pattern CALL = Pattern.compile("FUN_([0-9a-f]+)\\(");

    /** Follows thin wrappers until a function that itself reads named fields; 0 if none. */
    long findReader(long a, int levels) {
        String c = decomp(a);
        if (c.contains("\",0,")) return a;
        if (levels == 0 || c.length() > 4000) return 0;
        Matcher w = CALL.matcher(c);
        while (w.find()) {
            long r = findReader(Long.parseLong(w.group(1), 16), levels - 1);
            if (r != 0) return r;
        }
        return 0;
    }

    void walk(long a, String indent, int depth) {
        if (depth > 6) return;
        String c = decomp(a);
        Matcher m = FIELD.matcher(c);
        boolean any = false;
        while (m.find()) {
            any = true;
            out.println(indent + m.group(2) + "  off=" + m.group(3).trim() + "  via " + m.group(1));
            long reader = findReader(Long.parseLong(m.group(1).substring(4), 16), 4);
            if (reader != 0) walk(reader, indent + "  ", depth + 1);
        }
        if (!any && depth == 0) out.println(indent + "(no fields in " + Long.toHexString(a) + ")");
    }

    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        out = new PrintWriter(new FileWriter(a[0]));
        di = new DecompInterface();
        di.openProgram(currentProgram);
        for (int i = 1; i < a.length; i++) {
            out.println("== " + a[i]);
            walk(Long.parseLong(a[i], 16), "", 0);
        }
        out.close();
    }
}
