# Third-Party Notices

## SAP2000

La versione distribuita del plugin usa l'API di SAP2000/CSiBridge tramite `CSiAPIv1.dll`.

SAP2000, CSiBridge e le loro DLL API sono software proprietari di Computers and Structures, Inc. (CSI). Non fanno parte del pacchetto MESHing. Gli utenti devono ottenere i programmi e i componenti API direttamente da CSI nel rispetto della licenza applicabile.

Sito: <https://www.csiamerica.com/products/sap2000>

## Componenti del backend binario 3.0.0

Il codice originale FEMesh, ora MESHing, è GPL-2.0-or-later. Per il backend binario
combinato con OpenSSL 3 (Apache-2.0) viene esercitata l'opzione GPLv3
consentita dalle clausole «or later» del codice originale e di Gmsh. Questa
scelta non modifica la licenza di alcun componente di terzi.

| Componente | Licenza | Ruolo e riferimenti |
| --- | --- | --- |
| Gmsh 4.15.2 e binding Python | GPL-2.0-or-later con eccezione limitata dei titolari | Libreria di mesh nel backend; `licenses/GMSH-LICENSE.txt`, binding nel sorgente MESHing e `gmsh-4.15.2-source.tgz` tra gli asset della stessa release. |
| CPython 3.12.7 | Python Software Foundation License | Runtime autonomo; `licenses/PYTHON-LICENSE.txt`, [sorgente CPython](https://www.python.org/downloads/release/python-3127/). |
| OpenSSL 3.0.15 (`libcrypto-3.dll`) | Apache-2.0 | Componente del runtime Python; avviso nel testo Python e [sorgente OpenSSL](https://github.com/openssl/openssl/releases/tag/openssl-3.0.15). |
| libffi 3.4.4 (`libffi-8.dll`) | Licenza permissiva libffi (testo in `licenses/PYTHON-LICENSE.txt`) | Supporto FFI; [sorgente upstream](https://github.com/libffi/libffi/releases/tag/v3.4.4). Versione indicata dalla build ufficiale CPython 3.12.7; SHA-256 della DLL inclusa `eff52743773eb550fcc6ce3efc37c85724502233b6b002a35496d828bd7b280a`, identica alla DLL della distribuzione Python 3.12.7 utilizzata per il build. |
| bzip2 1.0.8 e liblzma di XZ Utils 5.2.5 | bzip2: licenza permissiva nel testo Python; liblzma: dominio pubblico secondo gli autori | Moduli `_bz2.pyd` e `_lzma.pyd` della distribuzione CPython; [sorgente bzip2](https://sourceware.org/bzip2/) e [condizioni XZ Utils 5.2.5](https://github.com/tukaani-project/xz/blob/v5.2.5/COPYING). Gli altri strumenti GPL di XZ Utils non sono distribuiti come componenti separati. |
| PyInstaller 6.16.0 (bootloader) | GPL-2.0 con eccezione per i programmi generati; alcuni file Apache-2.0 | Bootloader nel backend eseguibile; [sorgente e termini](https://github.com/pyinstaller/pyinstaller/releases/tag/v6.16.0). |

### Inventario della DLL Gmsh ufficiale distribuita

La DLL `gmsh-4.15.dll` è **identica byte per byte** a quella della wheel Windows ufficiale Gmsh 4.15.2. Le versioni in `General.BuildInfo` sono versioni dichiarate dalla build, non una distinta delle librerie linkate. Una stringa diagnostica nella DLL è riportata come evidenza **distinta** da un'opzione di build: non permette di attribuire da sola una revisione inesposta.

| Componente | Versione ed evidenza nella DLL | Licenza ed eccezione | Avviso/obbligo e fonte del sorgente | Progetto |
| --- | --- | --- | --- | --- |
| Open CASCADE Technology | 7.8.1 in `General.BuildInfo`; numerosi simboli `TopoDS_`/`opencascade` esportati dalla DLL, senza import di DLL OCC: evidenza diretta di codice OCC nella DLL, non ricetta di linking completa. | LGPL-2.1, eccezione OCCT **limitata all'uso degli header nell'object code**, non esenzione generale dal linking LGPL. | `licenses/OCCT-LGPL-2.1.txt`, `licenses/OCCT-LGPL-EXCEPTION.txt`; sorgente `third_party/native-sources/occt-7.8.1-source.tar.gz` nell'archivio sorgente MESHing, SHA-256 in `NATIVE-SOURCES.json`. | [Progetto e sorgente OCCT 7.8.1](https://github.com/Open-Cascade-SAS/OCCT/releases/tag/V7_8_1). |
| FLTK | 1.3.11 in `General.BuildInfo`, opzione `Fltk` e wrapper Gmsh esportato `GmshMainFLTK`: indicano supporto compilato, **non** provano da soli quali oggetti FLTK siano incorporati. | GNU Library GPL v2 con eccezione FLTK che consente il linking statico delle applicazioni; restano i termini per eventuali modifiche alla libreria e l'attribuzione. | `licenses/FLTK-LICENSE.txt`, sorgente `third_party/native-sources/fltk-1.3.11-source.tar.gz` nell'archivio sorgente MESHing; *Gmsh is based in part on the work of the FLTK project*. | [FLTK e sorgente 1.3.11](https://github.com/fltk/fltk/tree/release-1.3.11). |
| PETSc | 3.15.0 in `General.BuildInfo` e diagnostica nativa `PETSC ERROR` nella DLL; nessuna DLL PETSc importata. | Licenza permissiva PETSc. | Avviso originale `licenses/PETSC-LICENSE.txt` con copyright e condizioni; [sorgente upstream disponibile](https://gitlab.com/petsc/petsc/-/tags/v3.15.0), senza duplicarne l'archivio nella release MESHing. | [Progetto PETSc](https://petsc.org/). |
| MED file library | 4.1.0 in `General.BuildInfo` e diagnostica nativa `_MEDfileOpen.c` nella DLL; nessuna DLL MED importata. | LGPL-3.0-or-later. | `licenses/MED-LGPL-3.0.txt`, `licenses/MED-GPL-3.0.txt`; sorgente `third_party/native-sources/med-4.1.0-source.tar.gz` nell'archivio sorgente MESHing, controllato con SHA-512, origine in `NATIVE-SOURCES.json`. | [Progetto MED/SALOME](https://www.salome-platform.org/). |
| HDF5 | 1.10.5 nella stringa interna `HDF5 Version: 1.10.5` della DLL; non dichiarare la versione sulla base del solo CMake. | Licenza HDF Group con condizioni LBNL. | `licenses/HDF5-LICENSE.txt`, `licenses/HDF5-LBNL-LICENSE.txt`; [sorgente ufficiale disponibile](https://github.com/HDFGroup/hdf5/tree/hdf5-1_10_5), senza duplicarne l'archivio nella release MESHing. | [HDF Group](https://www.hdfgroup.org/). |
| CGNS | Versione interna **non esposta**; opzione `Cgns` in `General.BuildInfo` e diagnostica `CGNS file %d is not open` nella DLL, senza DLL CGNS importata. Le diagnostiche non identificano una revisione. | Licenza permissiva di tipo zlib per la libreria CGNS upstream; nessuna versione dedotta da nomi di funzioni. | `licenses/CGNS-LICENSE.txt` è un testo upstream di riferimento (v4.4.0), **non** una prova che quella revisione sia nella wheel. | [Progetto e sorgente CGNS](https://github.com/CGNS/CGNS). |
| MMG | **5.5.2**, verificata eseguendo la DLL Gmsh distribuita su una mesh isolata in memoria con `Mesh.Algorithm3D=7`: banner `MODULE MMG3D: 5.5.2 (Nov. 17, 2020)`. `General.BuildInfo` indica `Mmg`, ma **non** è stato usato da solo per dedurne la versione. Nessuna DLL MMG separata è importata. | **LGPL-3.0-or-later** secondo il file `LICENSE` dell'upstream v5.5.2. | `licenses/MMG-LICENSE.txt`, `licenses/MMG-LGPL-3.0.txt`, uguali byte per byte ai testi dell'archivio ufficiale; sorgente `mmg-5.5.2-source.tar.gz`, SHA-256 `3078a4e281640ae927223d449d37dfb63e0a7afc8ed737fc075eb6e255269a65` e `.sha256` nella medesima release. | [MmgTools: release e sorgenti v5.5.2](https://github.com/MmgTools/mmg/releases/tag/v5.5.2). |
| libpng | 1.6.48 nella diagnostica e nel copyright interni della DLL (`libpng version 1.6.48`); nessuna DLL PNG importata. | PNG Reference Library License v2, permissiva; conserva gli avvisi del sorgente. | Copyright presente nel binario; attribuzione nella documentazione aggiunta qui; [testo e sorgente ufficiale 1.6.48](https://github.com/pnggroup/libpng/tree/v1.6.48). | [Progetto libpng](https://libpng.org/). |
| JPEG / Independent JPEG Group | Versione della libreria **non esposta**; diagnostiche native `Wrong JPEG library version` e `JPEG parameter struct mismatch` nella DLL, senza DLL JPEG importata. | Licenza IJG permissiva; la distribuzione del solo eseguibile richiede la frase di attribuzione qui sotto. | Attribuzione inserita letteralmente; [condizioni e sorgenti ufficiali IJG](https://www.ijg.org/files/). | [Independent JPEG Group](https://www.ijg.org/). |

This software is based in part on the work of the Independent JPEG Group.

`General.BuildInfo` proviene dalla DLL Gmsh **effettivamente distribuita** e riporta le opzioni configurate nella build; non equivale a una distinta dei file oggetto effettivamente linkati. Stringhe diagnostiche, esportazioni PE, banner MMG3D di esecuzione e import dinamici sono indicati separatamente. `dumpbin /dependents` rileva soltanto 13 DLL Windows (fra cui `msvcrt.dll`, `KERNEL32.dll`, `OPENGL32.dll`): nessuna DLL esterna OCC/FLTK/PETSc/MED/HDF5/CGNS/MMG/PNG/JPEG è distribuita separatamente. Gli import PE non escludono caricamenti espliciti a runtime, né da soli dimostrano che ogni libreria indicata nelle opzioni sia stata incorporata. Nessuna revisione non esposta di CGNS è presentata come certa; non si dichiara una ricostruzione bit-per-bit della wheel upstream.

## Prerequisito Microsoft esterno

MESHing 3.0.0 **non include** `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll` né
altre DLL separate del runtime Visual C++ Microsoft. L'installer controlla
la versione del [Microsoft Visual C++ Redistributable x64 ufficiale](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)
(**14.42.34226.3 o successiva compatibile**). Se manca, lo scarica direttamente
da Microsoft e avvia separatamente il setup ufficiale, soggetto ai termini
Microsoft mostrati all'utente. MESHing non distribuisce l'eseguibile Microsoft.
La prova del prerequisito su una macchina Windows pulita rimane da effettuare.

L'additional permission del codice C# verso `CSiAPIv1.dll` non si applica a
Gmsh o agli altri componenti di terzi. MESHing redistribuisce la DLL **non
modificata** della wheel ufficiale Gmsh 4.15.2, il binding verificato e il
sorgente upstream Gmsh 4.15.2 con licenza e checksum fra gli asset della
stessa release. Le ulteriori fonti identificate sono riportate nel manifest
`NATIVE-SOURCES.json`; non si afferma una ricostruzione byte-per-byte della
build Windows ufficiale né si inventano revisioni non esposte da essa.

## Gmsh 4.15.2

I backend Python usano Gmsh 4.15.2.

Gmsh è distribuito dai suoi titolari secondo GNU GPL versione 2 o successiva, con un'eccezione specifica per i componenti elencati nel testo originale. Il pacchetto binario contiene il binding Python e `gmsh-4.15.dll` dalla wheel ufficiale Windows di Gmsh 4.15.2. Il testo originale è incluso in `licenses/GMSH-LICENSE.txt`. L'archivio sorgente Gmsh e quello MESHing/backend devono accompagnare il binario nella medesima release, con i rispettivi checksum. La scelta GPLv3 per il backend combinato con OpenSSL 3 non modifica la licenza originale o l'eccezione di Gmsh.

Wheel Windows ufficiale: <https://pypi.org/project/gmsh/4.15.2/>. SHA-256 della wheel `gmsh-4.15.2-py2.py3-none-win_amd64.whl`: `7b36083bb410fa27c5d0e052929d1a9844a5b09169d66017b72b41aabd49d711`. SHA-256 della DLL inclusa: `6cac3eefb477265d9fa60bbd869dbbbf7c7ca4cb308c0f8f2b43e91d43de3c1c`. Il binding usato corrisponde byte per byte al `gmsh.py` della medesima wheel, incluso nell'archivio dei sorgenti del plugin.

Sito, licenza e sorgenti: <https://gmsh.info/> e <https://gmsh.info/src/gmsh-4.15.2-source.tgz>

## Python

Il runtime autonomo incorpora CPython 3.12, distribuito secondo la Python Software Foundation License. Il testo applicabile e incluso in `licenses/PYTHON-LICENSE.txt`.

Il pacchetto binario incorpora `libcrypto-3.dll` (OpenSSL 3, Apache-2.0) e `libffi-8.dll` (libffi 3.4.4, licenza permissiva). `licenses/PYTHON-LICENSE.txt` riporta anche condizioni relative a codice Microsoft incorporato nella build Python; **l'assenza delle DLL Redistributable separate non significa assenza di codice Microsoft incorporato nei binari Python ufficiali**. I componenti conservano le rispettive licenze: non sono relicenziati sotto GPL dal progetto.

La versione inclusa è CPython **3.12.7** con OpenSSL **3.0.15** e libffi **3.4.4**, come indicato in [PCbuild/python.props di CPython 3.12.7](https://github.com/python/cpython/blob/v3.12.7/PCbuild/python.props). Il codice sorgente CPython è disponibile dalla [release Python 3.12.7](https://www.python.org/downloads/release/python-3127/), OpenSSL dalla [release 3.0.15](https://github.com/openssl/openssl/releases/tag/openssl-3.0.15). `licenses/PYTHON-LICENSE.txt` riporta avvisi bzip2, libffi e Apache-2.0 nonché condizioni sul codice Microsoft incorporato in Python; le DLL Visual C++ restano un prerequisito esterno. L'eccezione GPL di Gmsh non estende la propria autorizzazione ad altri componenti esterni.

Sito: <https://www.python.org/>

## PyInstaller

Il runtime autonomo e prodotto con PyInstaller 6.16.0. PyInstaller e distribuito secondo GPL-2.0 con una speciale eccezione per la distribuzione dei programmi generati; alcuni suoi file usano Apache-2.0. PyInstaller e uno strumento di build e non viene importato dal plugin a runtime.

Il bootloader PyInstaller nel file eseguibile mantiene i termini e l'eccezione dei suoi autori. Il sorgente dello strumento e il testo dell'eccezione sono disponibili presso [PyInstaller 6.16.0](https://github.com/pyinstaller/pyinstaller/releases/tag/v6.16.0); lo script di configurazione `packaging/SapGmshBackend.spec` è incluso nei sorgenti MESHing corrispondenti.

Sito e licenza: <https://pyinstaller.org/>

## Microsoft .NET

Il plugin usa .NET Framework 4.8 e Windows Forms. I relativi runtime e strumenti sono distribuiti da Microsoft secondo i rispettivi termini di licenza e non sono inclusi in questo pacchetto. Il plugin non contiene `SAP2000v1.dll`.
