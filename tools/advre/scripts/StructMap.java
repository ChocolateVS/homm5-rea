// Recursively maps Nival XML reader functions to absolute field offsets.
// A reader looks like:  FUN_xxx(param_2,"FieldName",0,param_1 + 0x10,param_1 + 0x10)
// (possibly through a temp: piVar1 = (int *)(param_1 + 4); FUN_004149b0(param_2,"F",0,piVar1,piVar1)).
// Primitive readers are leaves; other callees are wrappers whose inner struct reader is searched for.
// Usage: -postScript StructMap.java <outFile> <readerAddr> <baseHex> <prefix>
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import java.io.*;
import java.util.*;
import java.util.regex.*;

public class StructMap extends GhidraScript {
    DecompInterface di;
    PrintWriter out;
    Map<String, String> cache = new HashMap<>();
    static final Pattern CALL = Pattern.compile("(FUN_[0-9a-f]+)\\(param_2,\"([^\"]+)\",0,([^,]+),");
    static final Pattern ASSIGN = Pattern.compile("(\\w+) = \\([^)]*\\)\\(param_1 \\+ (0x[0-9a-f]+|\\d+)\\);");
    static final Pattern DIRECT = Pattern.compile("(?:\\([^)]*\\))?param_1 \\+ (0x[0-9a-f]+|\\d+)");

    String decomp(String fn) {
        if (cache.containsKey(fn)) return cache.get(fn);
        Function f = currentProgram.getListing().getFunctionAt(toAddr(fn.substring(4)));
        String c = "";
        if (f != null) {
            DecompileResults r = di.decompileFunction(f, 60, monitor);
            if (r.decompileCompleted()) c = r.getDecompiledFunction().getC();
        }
        cache.put(fn, c);
        return c;
    }

    boolean isReader(String c) { return CALL.matcher(c).find(); }

    // Find the struct reader reached from a wrapper (depth-limited search for a function with reader calls).
    String innerReader(String fn, int depth, Set<String> seen) {
        if (depth > 3 || !seen.add(fn)) return null;
        String c = decomp(fn);
        Matcher m = Pattern.compile("(FUN_[0-9a-f]+)\\(").matcher(c);
        List<String> callees = new ArrayList<>();
        while (m.find()) if (!m.group(1).equals(fn) && !callees.contains(m.group(1))) callees.add(m.group(1));
        for (String cal : callees) if (isReader(decomp(cal)) && !cal.equals("FUN_004149b0")) return cal;
        for (String cal : callees) { String r = innerReader(cal, depth + 1, seen); if (r != null) return r; }
        return null;
    }

    void map(String fn, long base, String prefix, int depth) {
        if (depth > 8) return;
        String c = decomp(fn);
        Map<String, Long> vars = new HashMap<>();
        for (String line : c.split("\n")) {
            Matcher a = ASSIGN.matcher(line);
            if (a.find()) vars.put(a.group(1), Long.decode(a.group(2)));
            Matcher m = CALL.matcher(line);
            if (!m.find()) continue;
            String callee = m.group(1), name = m.group(2), arg = m.group(3).trim();
            Long off = vars.get(arg);
            if (off == null) { Matcher d = DIRECT.matcher(arg); if (d.find()) off = Long.decode(d.group(1)); }
            if (off == null) { out.println("?? " + prefix + name + " arg=" + arg + " via " + callee); continue; }
            long abs = base + off;
            String type = switch (callee) {
                case "FUN_004149b0" -> "int";
                case "FUN_004a3d00" -> "float";
                case "FUN_004a3d50" -> "bool";
                default -> null;
            };
            if (type != null) { out.println(String.format("0x%05x %-6s %s%s", abs, type, prefix, name)); continue; }
            String inner = innerReader(callee, 0, new HashSet<>());
            out.println(String.format("0x%05x struct %s%s  (%s -> %s)", abs, prefix, name, callee, inner));
            if (inner != null) map(inner, abs, prefix + name + "/", depth + 1);
        }
    }

    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        out = new PrintWriter(new FileWriter(a[0]));
        di = new DecompInterface();
        di.openProgram(currentProgram);
        map("FUN_" + a[1].toLowerCase(), Long.parseLong(a[2], 16), a[3], 0);
        out.close();
    }
}
