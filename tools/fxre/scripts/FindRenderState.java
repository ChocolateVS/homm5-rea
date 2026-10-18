// Finds IDirect3DDevice9::SetRenderState calls (CALL [reg+0xe4]) whose state argument is one of
// the given D3DRENDERSTATETYPE values, printing the pushed immediate value(s) and the function.
// Usage: -postScript FindRenderState.java <outFile> <stateDec>...
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.io.*;
import java.util.*;
public class FindRenderState extends GhidraScript {
    @Override public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Set<Long> states = new HashSet<>();
        for (int i = 1; i < a.length; i++) states.add(Long.parseLong(a[i]));
        Listing l = currentProgram.getListing();
        InstructionIterator it = l.getInstructions(true);
        Deque<Instruction> window = new ArrayDeque<>();
        while (it.hasNext()) {
            Instruction ins = it.next();
            window.addLast(ins);
            if (window.size() > 8) window.removeFirst();
            if (!ins.getMnemonicString().equals("CALL")) continue;
            String op = ins.getDefaultOperandRepresentation(0);
            if (!op.contains("+ 0xe4]")) continue;
            // Collect pushes in the window (last pushed = first arg after this).
            List<String> pushes = new ArrayList<>();
            for (Instruction w : window) {
                if (w.getMnemonicString().equals("PUSH")) {
                    Object[] objs = w.getOpObjects(0);
                    pushes.add(objs.length > 0 && objs[0] instanceof Scalar s ? Long.toString(s.getUnsignedValue()) : w.getDefaultOperandRepresentation(0));
                }
            }
            // stdcall SetRenderState(this, State, Value): pushes in order Value, State, this
            if (pushes.size() < 2) continue;
            String state = pushes.size() >= 2 ? pushes.get(pushes.size() - 2) : "?";
            String value = pushes.size() >= 3 ? pushes.get(pushes.size() - 3) : "?";
            // Some code pushes this last via EAX; try both orders.
            for (String cand : new String[]{state, pushes.get(pushes.size() - 1)}) {
                try {
                    if (states.contains(Long.parseLong(cand))) {
                        Function f = l.getFunctionContaining(ins.getAddress());
                        out.println(ins.getAddress() + " state=" + cand + " pushes=" + pushes + " in " + (f == null ? "?" : f.getEntryPoint()));
                        break;
                    }
                } catch (NumberFormatException e) {}
            }
        }
        out.close();
    }
}
