// Finds functions whose instructions use memory operands with the given displacements.
// Usage: -postScript FindDisp.java <outFile> <minDistinct> <mnemonicPrefix|*> <hexDisp> [hexDisp...]
// Prints functions using at least minDistinct of the displacements, with the matching instructions.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.lang.OperandType;
import java.io.*;
import java.util.*;

public class FindDisp extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        int min = Integer.parseInt(a[1]);
        Set<Long> want = new HashSet<>();
        String pre = a[2];
        for (int i = 3; i < a.length; i++) want.add(Long.parseLong(a[i], 16));
        Listing l = currentProgram.getListing();
        Map<Function, TreeMap<Long, List<String>>> hits = new HashMap<>();
        for (Instruction ins : l.getInstructions(true)) {
            if (!pre.equals("*") && !ins.getMnemonicString().startsWith(pre)) continue;
            for (int op = 0; op < ins.getNumOperands(); op++) {
                int t = ins.getOperandType(op);
                if ((t & OperandType.DYNAMIC) == 0 && (t & OperandType.ADDRESS) == 0) continue;
                for (Object o : ins.getOpObjects(op)) {
                    if (o instanceof Scalar s && want.contains(s.getUnsignedValue())) {
                        Function f = l.getFunctionContaining(ins.getAddress());
                        if (f == null) continue;
                        hits.computeIfAbsent(f, k -> new TreeMap<>())
                            .computeIfAbsent(s.getUnsignedValue(), k -> new ArrayList<>())
                            .add(ins.getAddress() + "  " + ins);
                    }
                }
            }
        }
        List<Function> fs = new ArrayList<>(hits.keySet());
        fs.sort(Comparator.comparingInt(f -> -hits.get(f).size()));
        for (Function f : fs) {
            var m = hits.get(f);
            if (m.size() < min) continue;
            out.println("FUNC " + f.getName() + " @ " + f.getEntryPoint() + " distinct=" + m.size());
            for (var e : m.entrySet())
                for (String s : e.getValue()) out.println("   " + s);
        }
        out.close();
    }
}
