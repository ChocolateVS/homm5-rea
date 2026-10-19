// Lists "MOV byte ptr [reg+disp], x" instructions (byte stores) for the given displacements in [start,end].
// Usage: -postScript FindByteWrite.java <outFile> <start> <end> <hexDisp> [hexDisp...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import java.io.*;
import java.util.*;

public class FindByteWrite extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Address s = toAddr(a[1]), e = toAddr(a[2]);
        Set<String> want = new HashSet<>();
        for (int i = 3; i < a.length; i++) want.add("0x" + a[i].toLowerCase());
        Listing l = currentProgram.getListing();
        InstructionIterator it = l.getInstructions(new AddressSet(s, e), true);
        while (it.hasNext()) {
            Instruction ins = it.next();
            String t = ins.toString();
            if (!t.startsWith("MOV byte ptr [")) continue;
            int p = t.indexOf('+'), q = t.indexOf(']');
            if (p < 0 || q < p) continue;
            String disp = t.substring(p + 1, q).trim().toLowerCase();
            if (!want.contains(disp)) continue;
            Function f = l.getFunctionContaining(ins.getAddress());
            out.println(ins.getAddress() + "  " + t + "  in " + (f == null ? "?" : f.getName()));
        }
        out.close();
    }
}
