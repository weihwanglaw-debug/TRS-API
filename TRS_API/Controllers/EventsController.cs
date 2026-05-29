import { useState, useRef, useEffect } from "react";
import { useParams, useNavigate } from "react-router-dom";
import {
  ArrowLeft, Plus, Edit2, Users, Save, X, Image, Trash2,
  MoreVertical, ExternalLink, Lock, Unlock, FileText, GripVertical,
} from "lucide-react";
import type { TournamentEvent, Program, EventDocument } from "@/types/config";
import { getEventStatus } from "@/lib/eventUtils";
import StatusBadge from "@/components/events/StatusBadge";
import ProgramModal from "@/components/admin/ProgramModal";
import SeedingModal from "@/components/admin/SeedingModal";
import { Switch } from "@/components/ui/switch";
import { PageLoader } from "@/components/ui/LoadingSpinner";
import ActionDropdownPortal from "@/components/ui/ActionDropdownPortal";
import { ConfirmDialog } from "@/components/ui/ConfirmDialog";
import {
  apiGetEvent, apiCreateEvent, apiUpdateEvent, apiDeleteEvent,
  apiAddProgram, apiUpdateProgram, apiDeleteProgram, apiUpdateProgramStatus,
  apiAddEventDocument, apiUpdateEventDocument, apiDeleteEventDocument,
  apiUploadFile, assetUrl,
} from "@/lib/api";

// ── Tiptap rich-text editor ───────────────────────────────────────────────────
// Install: npm install @tiptap/react @tiptap/pm @tiptap/starter-kit @tiptap/extension-link
import { useEditor, EditorContent } from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import Link from "@tiptap/extension-link";

const MAX_IMAGE_MB = 2;
const ALLOWED_TYPES = ["image/jpeg", "image/png", "image/webp"];
const MAX_PDF_MB = 8;

function isBlobUrl(url: string) { return url.startsWith("blob:"); }

// ── Tiptap toolbar ────────────────────────────────────────────────────────────
function RichTextEditor({
  value,
  onChange,
  disabled,
}: {
  value: string;
  onChange: (html: string) => void;
  disabled: boolean;
}) {
  const editor = useEditor({
    extensions: [
      StarterKit,
      Link.configure({ openOnClick: false, autolink: true }),
    ],
    content: value,
    editable: !disabled,
    onUpdate: ({ editor }) => onChange(editor.getHTML()),
  });

  // Sync external value changes (e.g. when event loads)
  useEffect(() => {
    if (!editor) return;
    if (editor.getHTML() !== value) {
      editor.commands.setContent(value || "", { emitUpdate: false });
    }
  }, [value, editor]);

  // Sync editable state
  useEffect(() => {
    editor?.setEditable(!disabled);
  }, [disabled, editor]);

  if (!editor) return null;

  const btn = (active: boolean) =>
    `px-2 py-1 text-xs font-semibold transition-colors ${
      active
        ? "text-white"
        : "opacity-60 hover:opacity-100"
    }`;
  const activeStyle = (active: boolean): React.CSSProperties =>
    active ? { backgroundColor: "var(--color-primary)", color: "#fff" } : {};

  return (
    <div style={{ border: "1px solid var(--color-table-border)" }}>
      {!disabled && (
        <div
          className="flex flex-wrap gap-0.5 p-2"
          style={{ borderBottom: "1px solid var(--color-table-border)", backgroundColor: "var(--color-background-secondary)" }}
        >
          {/* Headings */}
          <button type="button"
            className={btn(editor.isActive("heading", { level: 2 }))}
            style={activeStyle(editor.isActive("heading", { level: 2 }))}
            onClick={() => editor.chain().focus().toggleHeading({ level: 2 }).run()}>H2</button>
          <button type="button"
            className={btn(editor.isActive("heading", { level: 3 }))}
            style={activeStyle(editor.isActive("heading", { level: 3 }))}
            onClick={() => editor.chain().focus().toggleHeading({ level: 3 }).run()}>H3</button>
          <span className="w-px mx-1 self-stretch" style={{ backgroundColor: "var(--color-table-border)" }} />
          {/* Inline */}
          <button type="button"
            className={btn(editor.isActive("bold"))}
            style={activeStyle(editor.isActive("bold"))}
            onClick={() => editor.chain().focus().toggleBold().run()}>B</button>
          <button type="button"
            className={btn(editor.isActive("italic"))}
            style={{ ...activeStyle(editor.isActive("italic")), fontStyle: "italic" }}
            onClick={() => editor.chain().focus().toggleItalic().run()}>I</button>
          <span className="w-px mx-1 self-stretch" style={{ backgroundColor: "var(--color-table-border)" }} />
          {/* Lists */}
          <button type="button"
            className={btn(editor.isActive("bulletList"))}
            style={activeStyle(editor.isActive("bulletList"))}
            onClick={() => editor.chain().focus().toggleBulletList().run()}>• List</button>
          <button type="button"
            className={btn(editor.isActive("orderedList"))}
            style={activeStyle(editor.isActive("orderedList"))}
            onClick={() => editor.chain().focus().toggleOrderedList().run()}>1. List</button>
          <span className="w-px mx-1 self-stretch" style={{ backgroundColor: "var(--color-table-border)" }} />
          {/* Link */}
          <button type="button"
            className={btn(editor.isActive("link"))}
            style={activeStyle(editor.isActive("link"))}
            onClick={() => {
              if (editor.isActive("link")) {
                editor.chain().focus().unsetLink().run();
              } else {
                const url = window.prompt("Enter URL");
                if (url) editor.chain().focus().setLink({ href: url }).run();
              }
            }}>Link</button>
          <span className="w-px mx-1 self-stretch" style={{ backgroundColor: "var(--color-table-border)" }} />
          <button type="button"
            className="px-2 py-1 text-xs opacity-60 hover:opacity-100"
            onClick={() => editor.chain().focus().undo().run()}>Undo</button>
          <button type="button"
            className="px-2 py-1 text-xs opacity-60 hover:opacity-100"
            onClick={() => editor.chain().focus().redo().run()}>Redo</button>
        </div>
      )}
      <EditorContent
        editor={editor}
        className="prose prose-sm max-w-none p-4 min-h-[180px] focus-within:outline-none"
        style={{
          fontSize: 13,
          color: "var(--color-body-text)",
          backgroundColor: disabled ? "var(--color-background-secondary)" : "var(--color-page-bg)",
        }}
      />
    </div>
  );
}

// ── Document manager row ──────────────────────────────────────────────────────
interface DocRow {
  id?: number;       // undefined = not yet saved to backend
  label: string;
  fileUrl: string;
  displayOrder: number;
  uploading?: boolean;
  labelError?: string;
}

export default function EventEdit() {
  const { eventId } = useParams();
  const navigate = useNavigate();
  const isNew = eventId === "new";

  const [event,    setEvent]   = useState<TournamentEvent | null>(null);
  const [loading,  setLoading] = useState(!isNew);
  const [saving,   setSaving]  = useState(false);
  const [apiError, setApiError] = useState("");

  // ── Form state ──────────────────────────────────────────────────────────────
  const [form, setForm] = useState({
    name: "", description: "", venue: "", venueAddress: "",
    eventStartDate: "", eventEndDate: "", openDate: "", closeDate: "",
    maxParticipants: 100, sponsorInfo: "", bannerUrl: "",
    additionalInfo: "",          // replaces prospectusUrl
    consentStatement: "",
    isSports: true, sportType: "Badminton",
    fixtureMode: "internal" as "internal" | "external" | "not_required",
  });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const set = <K extends keyof typeof form>(k: K, v: typeof form[K]) =>
    setForm(p => ({ ...p, [k]: v }));

  // ── Document rows ───────────────────────────────────────────────────────────
  const [docs, setDocs] = useState<DocRow[]>([]);

  // ── Gallery / banner ────────────────────────────────────────────────────────
  const [gallery,          setGallery]          = useState<string[]>([]);
  const [galleryError,     setGalleryError]     = useState("");
  const [uploadingGallery, setUploadingGallery] = useState(false);
  const [bannerError,      setBannerError]      = useState("");
  const [uploadingBanner,  setUploadingBanner]  = useState(false);
  const galleryRef = useRef<HTMLInputElement>(null);
  const bannerRef  = useRef<HTMLInputElement>(null);

  // ── Program / editing state ─────────────────────────────────────────────────
  const [editing,          setEditing]          = useState(isNew);
  const [programs,         setPrograms]         = useState<Program[]>([]);
  const [programModalOpen, setProgramModalOpen] = useState(false);
  const [editingProgram,   setEditingProgram]   = useState<Program | null>(null);
  const [seedingOpen,      setSeedingOpen]      = useState(false);
  const [seedingProgramId, setSeedingProgramId] = useState("");
  const [openAction,       setOpenAction]       = useState<{ prog: Program; anchorEl: HTMLElement } | null>(null);
  const [deleteConfirmOpen,setDeleteConfirmOpen]= useState(false);

  const RACKET_SPORTS = ["Badminton", "Tennis", "Squash", "Table Tennis", "Pickleball"];
  const TEAM_SPORTS   = ["Basketball", "Football", "Volleyball", "Rugby", "Hockey", "Netball"];
  const isRacketSport = form.isSports && RACKET_SPORTS.includes(form.sportType);
  const isTeamSport   = form.isSports && TEAM_SPORTS.includes(form.sportType);
  const isBadminton   = form.isSports && form.sportType === "Badminton";

  // ── Load existing event ─────────────────────────────────────────────────────
  useEffect(() => {
    if (isNew) return;
    apiGetEvent(eventId!).then(r => {
      if (r.error) { setApiError(r.error.message); return; }
      const ev = r.data!;
      setEvent(ev);
      setPrograms(ev.programs);
      const safeGallery = (ev.galleryUrls || []).filter(u => !isBlobUrl(u));
      if (safeGallery.length !== (ev.galleryUrls || []).length)
        setGalleryError("Some previously selected images were temporary previews and can't be loaded after refresh. Please re-upload them.");
      setGallery(safeGallery);
      // Populate document rows from loaded event
      setDocs((ev.documents || []).map(d => ({
        id: d.id, label: d.label, fileUrl: d.fileUrl, displayOrder: d.displayOrder,
      })));
      setForm({
        name:             ev.name,
        description:      ev.description || "",
        venue:            ev.venue,
        venueAddress:     ev.venueAddress || "",
        eventStartDate:   ev.eventStartDate,
        eventEndDate:     ev.eventEndDate || "",
        openDate:         ev.openDate,
        closeDate:        ev.closeDate,
        maxParticipants:  ev.maxParticipants || 100,
        sponsorInfo:      ev.sponsorInfo || "",
        bannerUrl:        ev.bannerUrl || "",
        additionalInfo:   ev.additionalInfo || "",
        consentStatement: ev.consentStatement || "",
        isSports:         ev.isSports ?? true,
        sportType:        ev.sportType || "Badminton",
        fixtureMode:      (ev.fixtureMode || "internal") as "internal" | "external" | "not_required",
      });
    }).finally(() => setLoading(false));
  }, [eventId, isNew]);

  // ── Gallery upload ──────────────────────────────────────────────────────────
  const handleGalleryUpload = (e: React.ChangeEvent<HTMLInputElement>) => {
    setGalleryError("");
    const files = Array.from(e.target.files || []);
    const errs: string[] = [];
    const newUrls: Array<Promise<string | null>> = [];
    files.forEach(f => {
      if (!ALLOWED_TYPES.includes(f.type)) { errs.push(`${f.name}: only JPG, PNG, WEBP allowed`); return; }
      if (f.size > MAX_IMAGE_MB * 1024 * 1024) { errs.push(`${f.name}: exceeds ${MAX_IMAGE_MB}MB limit`); return; }
      newUrls.push(
        apiUploadFile(f, "events/gallery").then(r => {
          if (r.error) { errs.push(`${f.name}: ${r.error.message}`); return null; }
          return r.data;
        }),
      );
    });
    if (errs.length) setGalleryError(errs.join(" · "));
    setUploadingGallery(true);
    void Promise.all(newUrls).then(urls => {
      const good = urls.filter(Boolean) as string[];
      if (errs.length) setGalleryError(errs.join(" · "));
      if (good.length) setGallery(prev => [...prev, ...good]);
    }).finally(() => setUploadingGallery(false));
    if (galleryRef.current) galleryRef.current.value = "";
  };

  const removeGalleryImage = (idx: number) => setGallery(prev => prev.filter((_, i) => i !== idx));

  // ── Banner upload ───────────────────────────────────────────────────────────
  const handleBannerUpload = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    setBannerError("");
    if (file.size > MAX_IMAGE_MB * 1024 * 1024) { setBannerError(`Banner image must be under ${MAX_IMAGE_MB}MB.`); return; }
    setUploadingBanner(true);
    apiUploadFile(file, "events/banner").then(r => {
      if (r.data) set("bannerUrl", r.data);
      else setBannerError(r.error?.message ?? "Upload failed.");
    }).finally(() => { setUploadingBanner(false); if (bannerRef.current) bannerRef.current.value = ""; });
  };

  // ── Document handlers ───────────────────────────────────────────────────────
  const addDocRow = () => {
    setDocs(prev => [...prev, { label: "", fileUrl: "", displayOrder: prev.length }]);
  };

  const updateDocRow = (idx: number, patch: Partial<DocRow>) => {
    setDocs(prev => prev.map((d, i) => i === idx ? { ...d, ...patch } : d));
  };

  const removeDocRow = async (idx: number) => {
    const doc = docs[idx];
    // If already saved to backend, delete it
    if (doc.id && !isNew && eventId) {
      await apiDeleteEventDocument(eventId, doc.id);
    }
    setDocs(prev => prev.filter((_, i) => i !== idx));
  };

  const handleDocFileUpload = async (idx: number, file: File) => {
    if (file.size > MAX_PDF_MB * 1024 * 1024) {
      updateDocRow(idx, { labelError: `File exceeds ${MAX_PDF_MB}MB.` });
      return;
    }
    updateDocRow(idx, { uploading: true, labelError: undefined });
    const r = await apiUploadFile(file, "events/documents");
    updateDocRow(idx, { uploading: false });
    if (r.error) { updateDocRow(idx, { labelError: r.error.message }); return; }
    updateDocRow(idx, { fileUrl: r.data! });

    // FIX: read fresh doc state via a no-op setter to avoid stale closure
    // after the async apiUploadFile call above.
    if (!isNew && eventId) {
      let currentDoc: DocRow | undefined;
      setDocs(prev => {
        currentDoc = prev[idx];
        return prev; // no mutation — reading only
      });
      if (currentDoc?.id) {
        const label = currentDoc.label || file.name.replace(/\.[^.]+$/, "");
        await apiUpdateEventDocument(eventId, currentDoc.id, {
          label, fileUrl: r.data!, displayOrder: currentDoc.displayOrder,
        });
      }
    }
  };

  // FIX: accept a docs snapshot at call time so saveDocuments always operates
  // on the state that was current when handleSave captured it, not a stale closure.
  const saveDocuments = async (savedEventId: string, docsSnapshot: DocRow[]) => {
    for (let i = 0; i < docsSnapshot.length; i++) {
      const doc = docsSnapshot[i];
      if (!doc.fileUrl) continue; // skip rows without a file
      const label = doc.label.trim() || `Document ${i + 1}`;
      if (doc.id) {
        await apiUpdateEventDocument(savedEventId, doc.id, {
          label, fileUrl: doc.fileUrl, displayOrder: i,
        });
      } else {
        const r = await apiAddEventDocument(savedEventId, {
          label, fileUrl: doc.fileUrl, displayOrder: i,
        });
        if (r.data) updateDocRow(i, { id: r.data.id, label });
      }
    }
  };

  // ── Validation ──────────────────────────────────────────────────────────────
  const validate = () => {
    const e: Record<string, string> = {};
    if (!form.name.trim()) e.name = "Required";
    if (!form.venue.trim()) e.venue = "Required";
    if (!form.eventStartDate) e.eventStartDate = "Required";
    if (!form.openDate) e.openDate = "Required";
    if (!form.closeDate) e.closeDate = "Required";
    if (form.eventEndDate && form.eventStartDate && form.eventEndDate < form.eventStartDate)
      e.eventEndDate = "Must be on or after start date";
    if (form.closeDate && form.eventStartDate && form.closeDate >= form.eventStartDate)
      e.closeDate = "Must be before event start date";
    if (form.openDate && form.closeDate && form.openDate >= form.closeDate)
      e.openDate = "Must be before close date";
    setErrors(e);
    return Object.keys(e).length === 0;
  };

  // ── Save ────────────────────────────────────────────────────────────────────
  const handleSave = async () => {
    if (!validate()) return;
    setSaving(true);
    setApiError("");
    try {
      const payload = { ...form, galleryUrls: gallery, programs };
      if (isNew) {
        const r = await apiCreateEvent(payload);
        if (r.error) { setApiError(r.error.message); return; }
        const newId = r.data!.id;
        for (const prog of programs) {
          const { id: _id, currentParticipants: _cp, participantSeeds: _ps, ...progPayload } = prog;
          void _id; void _cp; void _ps;
          const pr = await apiAddProgram(newId, progPayload);
          if (pr.error) { setApiError(`Event created but failed to save program "${prog.name}": ${pr.error.message}`); return; }
        }
        await saveDocuments(newId, docs);
        navigate("/admin/events");
      } else {
        const r = await apiUpdateEvent(eventId!, payload);
        if (r.error) { setApiError(r.error.message); return; }
        await saveDocuments(eventId!, docs);
        setEvent(r.data!);
        setEditing(false);
      }
    } finally {
      setSaving(false);
    }
  };

  const handleDeleteEvent = async () => {
    if (!eventId || isNew) return;
    setSaving(true);
    const r = await apiDeleteEvent(eventId);
    setSaving(false);
    if (r.error) { setApiError(r.error.message); return; }
    navigate("/admin/events");
  };

  const status = event ? getEventStatus(event) : undefined;

  if (loading) return <PageLoader label="Loading event…" />;
  if (!isNew && !event && !loading) return (
    <div className="py-20 text-center opacity-40 text-sm">Event not found.</div>
  );

  return (
    <div>
      {/* ── Sticky Header ── */}
      <div className="sticky-header px-2 md:px-4">
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-4">
            <button onClick={() => navigate("/admin/events")} className="btn-back">
              <ArrowLeft className="h-4 w-4" /> Back
            </button>
            <div>
              <h1 className="font-bold text-2xl">
                {isNew ? "Create New Event" : event?.name || "Event"}
              </h1>
              {status && <div className="mt-1"><StatusBadge status={status} /></div>}
            </div>
          </div>
          <div className="flex gap-3">
            {!isNew && !editing && (
              <>
                <button onClick={() => setDeleteConfirmOpen(true)} disabled={saving}
                  className="btn-outline flex items-center gap-2 px-5 py-2.5 text-sm font-medium"
                  style={{ color: "var(--badge-closed-text)", borderColor: "var(--badge-closed-text)" }}>
                  <Trash2 className="h-4 w-4" /> Delete Event
                </button>
                <button onClick={() => setEditing(true)}
                  className="btn-primary flex items-center gap-2 px-5 py-2.5 text-sm font-semibold">
                  <Edit2 className="h-4 w-4" /> Edit Event
                </button>
              </>
            )}
            {editing && (
              <>
                {!isNew && (
                  <button onClick={() => setEditing(false)}
                    className="btn-outline flex items-center gap-2 px-5 py-2.5 text-sm font-medium">
                    <X className="h-4 w-4" /> Cancel
                  </button>
                )}
                <button onClick={handleSave} disabled={saving}
                  className="btn-primary flex items-center gap-2 px-5 py-2.5 text-sm font-semibold disabled:opacity-50">
                  <Save className="h-4 w-4" /> {saving ? "Saving…" : "Save Event"}
                </button>
              </>
            )}
          </div>
        </div>
      </div>

      {apiError && (
        <div className="mb-6 px-4 py-3 text-sm font-medium"
          style={{ backgroundColor: "var(--badge-closed-bg)", color: "var(--badge-closed-text)", border: "1px solid var(--badge-closed-text)" }}>
          {apiError}
        </div>
      )}

      {/* ── Event Details ── */}
      <div className="mb-8 p-8" style={{ border: "1px solid var(--color-table-border)" }}>
        <SectionTitle>Event Details</SectionTitle>
        <div className="grid md:grid-cols-2 gap-6">
          <FF label="Event Name *" error={errors.name}>
            <input className="field-input" value={form.name} onChange={e => set("name", e.target.value)} disabled={!editing} />
          </FF>
          <FF label="Venue Name *" error={errors.venue}>
            <input className="field-input" value={form.venue} onChange={e => set("venue", e.target.value)} disabled={!editing} />
          </FF>
          <div className="md:col-span-2">
            <FF label="Venue Address">
              <input className="field-input" value={form.venueAddress} onChange={e => set("venueAddress", e.target.value)} disabled={!editing} />
            </FF>
          </div>
          <FF label="Event Start Date" error={errors.eventStartDate}>
            <input type="date" className="field-input" value={form.eventStartDate} onChange={e => set("eventStartDate", e.target.value)} disabled={!editing} />
          </FF>
          <FF label="Event End Date" error={errors.eventEndDate}>
            <input type="date" className="field-input" value={form.eventEndDate} onChange={e => set("eventEndDate", e.target.value)} disabled={!editing} />
          </FF>
          <FF label="Registration Open Date" error={errors.openDate}>
            <input type="date" className="field-input" value={form.openDate} onChange={e => set("openDate", e.target.value)} disabled={!editing} />
          </FF>
          <FF label="Registration Close Date" error={errors.closeDate}>
            <input type="date" className="field-input" value={form.closeDate} onChange={e => set("closeDate", e.target.value)} disabled={!editing} />
          </FF>
          <FF label="Max Participants">
            <input type="number" className="field-input" value={form.maxParticipants} onChange={e => set("maxParticipants", +e.target.value)} disabled={!editing} />
          </FF>
          <FF label="Sponsor Information">
            <input className="field-input" value={form.sponsorInfo} onChange={e => set("sponsorInfo", e.target.value)} disabled={!editing} />
          </FF>
          <div className="md:col-span-2">
            <FF label="Short Description (shown on event header)">
              <textarea className="field-input" rows={2} value={form.description} onChange={e => set("description", e.target.value)} disabled={!editing} />
            </FF>
          </div>
        </div>
      </div>

      {/* ── Documents ── */}
      <div className="mb-8 p-8" style={{ border: "1px solid var(--color-table-border)" }}>
        <div className="flex items-center justify-between mb-1">
          <SectionTitle>Documents</SectionTitle>
          {editing && (
            <button onClick={addDocRow}
              className="btn-outline flex items-center gap-2 px-4 py-2 text-sm font-medium">
              <Plus className="h-4 w-4" /> Add Document
            </button>
          )}
        </div>
        <p className="text-xs opacity-60 mb-5">
          Upload PDFs for download (prospectus, visa form, hotel form, etc.) · max {MAX_PDF_MB}MB each
        </p>

        {docs.length === 0 && (
          <p className="text-sm opacity-40">{editing ? "No documents yet — click Add Document." : "No documents uploaded."}</p>
        )}

        <div className="space-y-3">
          {docs.map((doc, idx) => (
            <div key={idx} className="flex items-start gap-3 p-3"
              style={{ border: "1px solid var(--color-table-border)", backgroundColor: "var(--color-background-secondary)" }}>
              {editing && <GripVertical className="h-4 w-4 mt-2.5 opacity-30 flex-shrink-0" />}

              {/* Label */}
              <div className="flex-1 min-w-0">
                <input
                  className="field-input mb-1"
                  placeholder="Label, e.g. Prospectus, Visa Application Form…"
                  value={doc.label}
                  disabled={!editing}
                  onChange={e => updateDocRow(idx, { label: e.target.value })}
                />
                {doc.labelError && (
                  <p className="text-xs" style={{ color: "var(--badge-open-text)" }}>{doc.labelError}</p>
                )}
              </div>

              {/* File */}
              <div className="flex-shrink-0">
                {editing ? (
                  <label className={`inline-flex items-center gap-2 btn-outline px-3 py-2 text-xs font-medium cursor-pointer ${doc.uploading ? "opacity-60 pointer-events-none" : ""}`}>
                    <FileText className="h-3.5 w-3.5" />
                    {doc.uploading ? "Uploading…" : doc.fileUrl ? "Replace" : "Choose PDF"}
                    <input
                      type="file" accept="application/pdf,.pdf" className="hidden"
                      disabled={doc.uploading}
                      onChange={e => { const f = e.target.files?.[0]; if (f) handleDocFileUpload(idx, f); e.target.value = ""; }}
                    />
                  </label>
                ) : doc.fileUrl ? (
                  <a href={assetUrl(doc.fileUrl)} target="_blank" rel="noreferrer"
                    className="inline-flex items-center gap-1 text-xs underline"
                    style={{ color: "var(--color-primary)" }}>
                    <FileText className="h-3.5 w-3.5" /> View
                  </a>
                ) : (
                  <span className="text-xs opacity-40">No file</span>
                )}
                {doc.fileUrl && !editing && (
                  <span className="block text-xs opacity-40 mt-0.5 truncate max-w-[160px]">
                    {doc.fileUrl.split("/").pop()}
                  </span>
                )}
              </div>

              {/* Remove */}
              {editing && (
                <button onClick={() => removeDocRow(idx)} className="mt-2 p-1 flex-shrink-0 opacity-50 hover:opacity-100"
                  style={{ color: "var(--badge-closed-text)" }}>
                  <X className="h-4 w-4" />
                </button>
              )}
            </div>
          ))}
        </div>
      </div>

      {/* ── Additional Information (rich text) ── */}
      <div className="mb-8 p-8" style={{ border: "1px solid var(--color-table-border)" }}>
        <SectionTitle>Additional Information</SectionTitle>
        <p className="text-xs opacity-60 mb-4">
          Free-form content shown on the event page — key dates, venue details, important notes, etc.
          Use headings to create sections.
        </p>
        <RichTextEditor
          value={form.additionalInfo}
          onChange={html => set("additionalInfo", html)}
          disabled={!editing}
        />
      </div>

      {/* ── Sport / Fixture Settings ── */}
      <div className="mb-8 p-8" style={{ border: "1px solid var(--color-table-border)" }}>
        <SectionTitle>Sport &amp; Fixture Settings</SectionTitle>
        <div className="space-y-5">
          <label className="flex items-center gap-3 text-sm cursor-pointer">
            <Switch checked={form.isSports} disabled={!editing}
              onCheckedChange={checked => set("isSports", !!checked)} />
            This is a sports event
          </label>
          {form.isSports && (
            <div className="grid sm:grid-cols-2 gap-6">
              <FF label="Sport Type">
                <select className="field-input" value={form.sportType} disabled={!editing}
                  onChange={e => set("sportType", e.target.value)}>
                  <optgroup label="Racket Sports">
                    <option>Badminton</option><option>Tennis</option><option>Squash</option>
                    <option>Table Tennis</option><option>Pickleball</option>
                  </optgroup>
                  <optgroup label="Team Sports">
                    <option>Basketball</option><option>Football</option><option>Volleyball</option>
                    <option>Rugby</option><option>Hockey</option><option>Netball</option>
                  </optgroup>
                  <optgroup label="Individual Sports">
                    <option>Swimming</option><option>Athletics</option><option>Gymnastics</option>
                    <option>Cycling</option><option>Archery</option>
                  </optgroup>
                  <optgroup label="Other"><option>Other</option></optgroup>
                </select>
              </FF>
              <FF label="Fixture Management Mode">
                {editing ? (
                  <div className="flex gap-0">
                    {([
                      { value: "internal",     label: "Internal (Built-in)" },
                      { value: "external",     label: "External System" },
                      { value: "not_required", label: "Not Required" },
                    ] as const).map(opt => (
                      <button key={opt.value} type="button" onClick={() => set("fixtureMode", opt.value)}
                        className="px-4 py-2.5 text-sm font-semibold transition-colors"
                        style={{
                          backgroundColor: form.fixtureMode === opt.value ? "var(--color-primary)" : "transparent",
                          color: form.fixtureMode === opt.value ? "var(--color-hero-text)" : "var(--color-body-text)",
                          border: `1px solid ${form.fixtureMode === opt.value ? "var(--color-primary)" : "var(--color-table-border)"}`,
                        }}>
                        {opt.label}
                      </button>
                    ))}
                  </div>
                ) : (
                  <p className="text-sm font-medium">
                    {form.fixtureMode === "internal" ? "Internal (Built-in)" : form.fixtureMode === "external" ? "External System" : "Not Required"}
                  </p>
                )}
              </FF>
            </div>
          )}
        </div>
      </div>

      {/* ── Event Banner ── */}
      <div className="mb-8 p-8" style={{ border: "1px solid var(--color-table-border)" }}>
        <SectionTitle>Event Banner</SectionTitle>
        <p className="text-xs opacity-60 mb-4">Hero background on the event page (JPG, PNG, WEBP · max {MAX_IMAGE_MB}MB)</p>
        {editing && (
          <>
            <label className={`inline-flex items-center gap-2 btn-outline px-5 py-2.5 text-sm font-medium cursor-pointer mb-3 ${uploadingBanner ? "opacity-60 pointer-events-none" : ""}`}>
              <Image className="h-4 w-4" /> {uploadingBanner ? "Uploading…" : form.bannerUrl ? "Replace Banner" : "Upload Banner"}
              <input ref={bannerRef} type="file" accept="image/jpeg,image/png,image/webp" className="hidden" onChange={handleBannerUpload} />
            </label>
            {bannerError && <p className="text-xs mb-3" style={{ color: "var(--badge-open-text)" }}>{bannerError}</p>}
          </>
        )}
        {form.bannerUrl ? (
          <div className="relative group" style={{ maxWidth: 640 }}>
            <img src={assetUrl(form.bannerUrl)} alt="Event banner" className="w-full object-cover"
              style={{ maxHeight: 220, border: "1px solid var(--color-table-border)" }} />
            {editing && (
              <button onClick={() => { set("bannerUrl", ""); setBannerError(""); }}
                className="absolute top-2 right-2 p-1.5 opacity-0 group-hover:opacity-100 transition-opacity"
                style={{ backgroundColor: "var(--badge-open-bg)", color: "var(--badge-open-text)" }}>
                <Trash2 className="h-3.5 w-3.5" />
              </button>
            )}
          </div>
        ) : (
          <p className="text-sm opacity-40">No banner uploaded.</p>
        )}
      </div>

      {/* ── Gallery ── */}
      <div className="mb-8 p-8" style={{ border: "1px solid var(--color-table-border)" }}>
        <SectionTitle>Event Gallery</SectionTitle>
        <p className="text-xs opacity-60 mb-4">Upload multiple images (JPG, PNG, WEBP · max {MAX_IMAGE_MB}MB each)</p>
        {editing && (
          <>
            <label className={`inline-flex items-center gap-2 btn-outline px-5 py-2.5 text-sm font-medium cursor-pointer mb-3 ${uploadingGallery ? "opacity-60 pointer-events-none" : ""}`}>
              <Image className="h-4 w-4" /> {uploadingGallery ? "Uploading…" : "Upload Images"}
              <input ref={galleryRef} type="file" multiple accept="image/jpeg,image/png,image/webp" className="hidden" onChange={handleGalleryUpload} />
            </label>
            {galleryError && <p className="text-xs mb-3" style={{ color: "var(--badge-open-text)" }}>{galleryError}</p>}
          </>
        )}
        {gallery.length > 0 ? (
          <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 gap-3">
            {gallery.map((url, i) => (
              <div key={i} className="relative group aspect-video overflow-hidden"
                style={{ border: "1px solid var(--color-table-border)" }}>
                <img src={assetUrl(url)} alt={`Gallery ${i + 1}`} className="w-full h-full object-cover" />
                {editing && (
                  <button onClick={() => removeGalleryImage(i)}
                    className="absolute top-1 right-1 p-1 opacity-0 group-hover:opacity-100 transition-opacity"
                    style={{ backgroundColor: "var(--badge-open-bg)", color: "var(--badge-open-text)" }}>
                    <Trash2 className="h-3.5 w-3.5" />
                  </button>
                )}
              </div>
            ))}
          </div>
        ) : (
          <p className="text-sm opacity-40">No images uploaded yet.</p>
        )}
      </div>

      {/* ── Consent Statement ── */}
      <div className="mb-8 p-8" style={{ border: "1px solid var(--color-table-border)" }}>
        <SectionTitle>Consent Statement</SectionTitle>
        <p className="text-xs opacity-60 mb-4">Shown to participants at registration. Leave blank to use the default.</p>
        <FF label="Consent Statement">
          <textarea className="field-input" rows={4} value={form.consentStatement}
            onChange={e => set("consentStatement", e.target.value)} disabled={!editing} />
        </FF>
      </div>

      {/* ── Programs ── */}
      <div className="mb-8">
        <div className="flex items-center justify-between mb-6">
          <SectionTitle>Programs</SectionTitle>
          <button onClick={() => { setEditingProgram(null); setProgramModalOpen(true); }}
            className="btn-primary flex items-center gap-2 px-5 py-2.5 text-sm font-semibold">
            <Plus className="h-4 w-4" /> Add Program
          </button>
        </div>
        {isNew && programs.length === 0 && (
          <div className="p-5 text-sm opacity-60 text-center" style={{ border: "1px dashed var(--color-table-border)" }}>
            Save the event first, then add programs — or add programs now and save everything together.
          </div>
        )}
        {programs.length > 0 && (
          <div className="overflow-x-auto" style={{ border: "1px solid var(--color-table-border)" }}>
            <table className="trs-table">
              <thead>
                <tr>
                  <th>Program Name</th><th>Format</th><th>Age</th><th>Gender</th>
                  <th>Fee</th><th>Status</th><th>Min / Max</th><th>Filled</th><th>Actions</th>
                </tr>
              </thead>
              <tbody>
                {programs.map(prog => (
                  <tr key={prog.id}>
                    <td className="font-medium">{prog.name}</td>
                    <td className="text-sm">{prog.type}</td>
                    <td className="text-sm">{prog.minAge}–{prog.maxAge}</td>
                    <td className="text-sm">{prog.gender}</td>
                    <td className="font-semibold text-sm" style={{ color: "var(--color-primary)" }}>
                      {prog.fee > 0 ? `$${prog.fee.toFixed(2)}` : "Free"}
                    </td>
                    <td>
                      <span className="text-xs font-semibold px-2 py-0.5"
                        style={{
                          backgroundColor: prog.status === "closed" ? "var(--badge-closed-bg)" : "var(--badge-open-bg)",
                          color: prog.status === "closed" ? "var(--badge-closed-text)" : "var(--badge-open-text)",
                        }}>
                        {prog.status === "closed" ? "Closed" : "Open"}
                      </span>
                    </td>
                    <td className="text-sm">{prog.minParticipants} / {prog.maxParticipants}</td>
                    <td className="text-sm">
                      <span>{prog.currentParticipants} / {prog.maxParticipants}</span>
                      <div className="h-1 mt-1 w-20" style={{ backgroundColor: "var(--color-table-border)" }}>
                        <div className="h-1 transition-all" style={{
                          width: `${Math.min(100, (prog.currentParticipants / prog.maxParticipants) * 100)}%`,
                          backgroundColor: prog.currentParticipants >= prog.maxParticipants ? "var(--badge-open-text)" : "var(--color-primary)",
                        }} />
                      </div>
                    </td>
                    <td>
                      <div className="relative">
                        <button
                          onClick={e => setOpenAction(openAction?.prog.id === prog.id ? null : { prog, anchorEl: e.currentTarget })}
                          className="p-2 hover:opacity-70" style={{ color: "var(--color-primary)" }}>
                          <MoreVertical className="h-4 w-4" />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {openAction && (
        <ActionDropdownPortal open={!!openAction} anchorEl={openAction.anchorEl} onClose={() => setOpenAction(null)}>
          <button onClick={() => { setEditingProgram(openAction.prog); setProgramModalOpen(true); setOpenAction(null); }}>
            <Edit2 className="h-4 w-4" /> Edit Program
          </button>
          {!isNew && (
            <button onClick={async () => {
              const prog = openAction.prog;
              const newStatus = prog.status === "closed" ? "open" : "closed";
              const r = await apiUpdateProgramStatus(eventId!, prog.id, newStatus);
              if (r.data) setPrograms(prev => prev.map(p => p.id === prog.id ? { ...p, status: newStatus } : p));
              setOpenAction(null);
            }}>
              {openAction.prog.status === "closed"
                ? <><Unlock className="h-4 w-4" /> Reopen Program</>
                : <><Lock   className="h-4 w-4" /> Close Program</>}
            </button>
          )}
          {!isNew && (
            <button onClick={() => {
              navigate(`/admin/registrations/participants?eventId=${eventId}&programId=${openAction.prog.id}`);
              setOpenAction(null);
            }}>
              <ExternalLink className="h-4 w-4" /> View Participants
            </button>
          )}
        </ActionDropdownPortal>
      )}

      <ProgramModal
        open={programModalOpen}
        onClose={() => { setProgramModalOpen(false); setEditingProgram(null); }}
        onSave={async (savedProgram: Program) => {
          if (!isNew && eventId && eventId !== "new") {
            if (editingProgram) {
              const r = await apiUpdateProgram(eventId, savedProgram.id, savedProgram);
              if (r.data) setPrograms(prev => prev.map(p => p.id === r.data!.id ? r.data! : p));
            } else {
              const { id: _id, currentParticipants: _cp, participantSeeds: _ps, ...payload } = savedProgram;
              void _id; void _cp; void _ps;
              const r = await apiAddProgram(eventId, payload);
              if (r.data) setPrograms(prev => [...prev, r.data!]);
            }
          } else {
            if (editingProgram) {
              setPrograms(prev => prev.map(p => p.id === savedProgram.id ? savedProgram : p));
            } else {
              setPrograms(prev => [...prev, {
                ...savedProgram,
                id: `prog-temp-${Date.now().toString(36)}`,
                currentParticipants: 0,
                participantSeeds: [],
              }]);
            }
          }
          setProgramModalOpen(false);
          setEditingProgram(null);
        }}
        program={editingProgram}
        isBadminton={isBadminton}
        isRacketSport={isRacketSport}
        isTeamSport={isTeamSport}
        sportType={form.sportType}
      />

      <SeedingModal open={seedingOpen} onClose={() => setSeedingOpen(false)}
        eventId={eventId ?? ""} programId={seedingProgramId} />

      <ConfirmDialog
        open={deleteConfirmOpen}
        onOpenChange={setDeleteConfirmOpen}
        title="Delete Event"
        description="Delete this event permanently? This cannot be undone."
        confirmLabel="Delete Event"
        loading={saving}
        destructive
        onConfirm={handleDeleteEvent}
      />
    </div>
  );
}

function SectionTitle({ children }: { children: React.ReactNode }) {
  return (
    <h2 className="font-bold text-base mb-5 pb-3"
      style={{ borderBottom: "1px solid var(--color-table-border)", color: "var(--color-heading)" }}>
      {children}
    </h2>
  );
}

function FF({ label, error, children }: { label: string; error?: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="block text-xs font-semibold mb-2 opacity-70">{label}</label>
      {children}
      {error && <p className="text-xs mt-1" style={{ color: "var(--badge-open-text)" }}>{error}</p>}
    </div>
  );
}